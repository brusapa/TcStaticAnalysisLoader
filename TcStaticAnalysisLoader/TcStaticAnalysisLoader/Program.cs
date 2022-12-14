/* 
The MIT License(MIT)

Copyright(c) 2018 Jakob Sagatowski

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using CommandLine;
using EnvDTE80;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using TCatSysManagerLib;

namespace AllTwinCAT.TcStaticAnalysisLoader
{
    class Program
    {
        private enum ExitValues : int
        {
            RunFailed = -1,
            RunOkAndNoErrors = 0,
            RunOkAndErrors = 1,
            RunOkAndWarnings = 2
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Create a logger
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<Program>();

            Options options = new();
            var result = Parser.Default.ParseArguments<Options>(args)
                                .WithParsed(o => options = o);
            // If help requested end the execution
            if (result.Tag == ParserResultType.NotParsed)
            {
                Environment.Exit((int)ExitValues.RunFailed);
            }

            logger?.LogDebug("TcStaticAnalysisLoader.exe : argument 1: {vsPath}", options.VisualStudioSolutionFilePath);
            logger?.LogDebug("TcStaticAnalysisLoader.exe : argument 2: {tsPath}", options.TwincatProjectFilePath);

            // If the paths are relatives, resolve them
            if (!Path.IsPathRooted(options.VisualStudioSolutionFilePath))
            {
                options.VisualStudioSolutionFilePath = Path.GetFullPath(options.VisualStudioSolutionFilePath);
            }
            if (!Path.IsPathRooted(options.TwincatProjectFilePath))
            {
                options.TwincatProjectFilePath = Path.GetFullPath(options.TwincatProjectFilePath);
            }
            if (!Path.IsPathRooted(options.ReportPath))
            {
                options.ReportPath = Path.GetFullPath(options.ReportPath);
            }

            /* Verify that the Visual Studio solution file and TwinCAT project file exists.*/
            if (!File.Exists(options.VisualStudioSolutionFilePath))
            {
                logger?.LogError("ERROR: Visual studio solution {vsPath} does not exist!", options.VisualStudioSolutionFilePath);
                Environment.Exit((int)ExitValues.RunFailed);
            }
            if (!File.Exists(options.TwincatProjectFilePath))
            {
                logger?.LogError("ERROR : TwinCAT project file {tsPath} does not exist!", options.TwincatProjectFilePath);
                Environment.Exit((int)ExitValues.RunFailed);
            }

            /* Find TwinCAT project version */
            var tcVersion = GetTwinCATVersion(options.TwincatProjectFilePath, logger);

            /* Make sure TwinCAT version is at minimum version 3.1.4022.0 as the static code
             * analysis tool is only supported from this version and onward
             */
            const string MIN_TC_VERSION_FOR_SC_ANALYSIS = "3.1.4022.0";
            var versionMin = new Version(MIN_TC_VERSION_FOR_SC_ANALYSIS);
            var versionDetected = new Version(tcVersion);
            var compareResult = versionDetected.CompareTo(versionMin);
            if (compareResult < 0)
            {
                logger?.LogError("The detected TwinCAT version in the project does not support TE1200 static code analysis\n" +
                    "The minimum version that supports TE1200 is {version}", MIN_TC_VERSION_FOR_SC_ANALYSIS);
                Environment.Exit((int)ExitValues.RunFailed);
            }

            MessageFilter.Register();

            // Generate DTE for VS solution
            var dte = GetDTEFromVisualStudioSolution(options.VisualStudioSolutionFilePath, logger);

            ITcRemoteManager remoteManager = (ITcRemoteManager)dte.GetObject("TcRemoteManager");
            remoteManager.Version = tcVersion;
            ITcAutomationSettings settings = (ITcAutomationSettings)dte.GetObject("TcAutomationSettings");
            settings.SilentMode = true; // Only available from TC3.1.4020.0 and above

            // Search for PLC programs
            var plcProgram = GetPlcProject(dte, options.TwincatProjectFilePath, logger);
            plcProgram.RunStaticAnalysis(false);

            // Get active errors
            var report = new ErrorReport(options.VisualStudioSolutionFilePath, options.TwincatProjectFilePath);
            var comErrors = dte.ToolWindows.ErrorList.ErrorItems;
            for (var ii = 1; ii < comErrors.Count + 1; ii++)
            {
                if (comErrors.Item(ii).ErrorLevel != vsBuildErrorLevel.vsBuildErrorLevelLow)
                {
                    report.AddError(new VisualStudioError(comErrors.Item(ii), options.VisualStudioSolutionFilePath));
                }
            }

            // Print all SA errors on screen
            var staticAnalyzerErrors = report.StaticAnalyzerErrors.ToList();
            staticAnalyzerErrors.ForEach(e => logger?.LogInformation("Description: {description}\n" +
                        "ErrorLevel: {errorLevel}\n" +
                        "Filename: {fileName}",
                        e.Description, e.ErrorLevel, e.Location.FileName));

            dte.Quit();

            MessageFilter.Revoke();

            // Write error report if required
            if (!string.IsNullOrEmpty(options.ReportPath))
            {
                switch(options.ReportFormat.ToLower())
                {
                    case "default":
                        File.WriteAllText(options.ReportPath, report.ToJson());
                        break;
                    case "gitlab":
                        File.WriteAllText(options.ReportPath, new GitlabCI.CodeQuality.Report(report).ToJson());
                        break;
                    default:
                        logger?.LogWarning("Unrecognized report format option: {format}", options.ReportFormat);
                        break;
                }
                
            }

            /* Return the result to the user */
            if (staticAnalyzerErrors.Any(e => e.ErrorLevel == VisualStudioErrorLevel.High))
                Environment.Exit((int)ExitValues.RunOkAndErrors);
            else if (staticAnalyzerErrors.Any(e => e.ErrorLevel == VisualStudioErrorLevel.Medium))
                Environment.Exit((int)ExitValues.RunOkAndWarnings);
            else
                Environment.Exit((int)ExitValues.RunOkAndNoErrors);
        }

        public static ITcPlcIECProject3 GetPlcProject(DTE2 dte, string projectPath, ILogger? logger = null)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            ITcSysManager3? twincatProject = null;
            foreach (EnvDTE.Project project in dte.Solution.Projects)
            {
                if (project.Name == projectName)
                {
                    twincatProject = (ITcSysManager3)project.Object;
                    break;
                }
            }
            if (twincatProject == null)
            {
                var errorString = $"Project {projectName} not found in solution";
                logger?.LogError(errorString);
                throw new Exception(errorString);
            }
            var tree = twincatProject.LookupTreeItem("TIPC");
            var name = tree.Child[1].Name;
            return (ITcPlcIECProject3)twincatProject.LookupTreeItem($"TIPC^{name}^{name} Project");
        }


        private static List<ITcPlcIECProject3> FindPLCProjects(ITcSysManager sysManager)
        {
            var plcProjectsList = new List<ITcPlcIECProject3>();
            var tree = sysManager.LookupTreeItem("TIPC");
            for (var ii = 0; ii < tree.ChildCount; ii++)
            {
                var name = tree.Child[ii + 1].Name;
                plcProjectsList.Add((ITcPlcIECProject3)sysManager.LookupTreeItem($"TIPC^{name}^{name} Project"));
            }
            return plcProjectsList;
        }

        private static DTE2 GetDTEFromVisualStudioSolution(string visualStudioSolutionFilePath, ILogger? logger = null)
        {
            // Get Visual Studio version
            var vsVersion = GetVisualStudioVersion(visualStudioSolutionFilePath, logger);

            /* Make sure the DTE loads with the same version of Visual Studio as the
             * TwinCAT project was created in
             */
            string VisualStudioProgId = "TcXaeShell.DTE." + vsVersion;
            var type = Type.GetTypeFromProgID(VisualStudioProgId);
            if (type == null)
            {
                logger?.LogError("Unable to obtain the type of visual studio program id\n" +
                    "The needed version was {version}", VisualStudioProgId);
                Environment.Exit((int)ExitValues.RunFailed);
            }
            var instanceObject = Activator.CreateInstance(type);
            if (instanceObject == null)
            {
                logger?.LogError("Unable to create a visual studio instance\n" +
                    "The needed version was {version}", VisualStudioProgId);
                Environment.Exit((int)ExitValues.RunFailed);
            }
            DTE2 dte = (DTE2)instanceObject;

            dte.SuppressUI = true;
            dte.MainWindow.Visible = false;
            dte.Solution.Open(visualStudioSolutionFilePath);
            return dte;
        }

        private static string GetVisualStudioVersion(string visualStudioSolutionFilePath, ILogger? logger = null)
        {
            var vsVersionRegex = new Regex(@"^VisualStudioVersion\s*=\s*(\d+.\d+)", RegexOptions.Multiline);
            var match = vsVersionRegex.Match(File.ReadAllText(visualStudioSolutionFilePath));
            if (match.Groups.Count < 2)
            {
                logger?.LogError("Did not find Visual studio version in Visual studio solution file");
                Environment.Exit((int)ExitValues.RunFailed);
            }
            logger?.LogInformation("In Visual Studio solution file, found visual studio version {version}", match.Groups[1].Value);
            return match.Groups[1].Value;
        }

        private static string GetTwinCATVersion(string twincatProjectFilePath, ILogger? logger = null)
        {
            var tcVersionRegex = new Regex("TcVersion\\s*=\\s*\"(\\d.+)?\"", RegexOptions.Multiline);
            var match = tcVersionRegex.Match(File.ReadAllText(twincatProjectFilePath));
            if (match.Groups.Count < 2)
            {
                logger?.LogError("Did not find TcVersion in TwinCAT project file");
                Environment.Exit((int)ExitValues.RunFailed);
            }
            logger?.LogInformation("In TwinCAT project file, found version {version}", match.Groups[1].Value);
            return match.Groups[1].Value;
        }
    }
}
