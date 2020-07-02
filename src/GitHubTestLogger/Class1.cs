﻿#define ENABLE_GH_API

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Octokit;

namespace GitHubTestLogger {

    [FriendlyName(FriendlyName)]
    [ExtensionUri(ExtensionUri)]
    public class GitHubTestLogger : ITestLoggerWithParameters {
        /// <summary>Uri used to uniquely identify the logger.</summary>
        public const string ExtensionUri = "logger://github.com/dogguts/GitHubTestLogger";

        /// <summary>Alternate user friendly string to uniquely identify the console logger.</summary>
        public const string FriendlyName = "github";

        /// <summary>Name of the Check Run/Job</summary>
        private const string CheckRunName = "test-report";

        private readonly string[] MandatoryVars = { "GITHUB_REPOSITORY_OWNER", "GITHUB_TOKEN", "GITHUB_SHA", "GITHUB_REPOSITORY" };

        private IDictionary<string, string> Vars = new Dictionary<string, string>();

        private string GITHUB_REPOSITORY_OWNER = string.Empty;
        private string GITHUB_REPOSITORY_NAME = string.Empty;
        private string GITHUB_TOKEN = string.Empty;
        private string GITHUB_SHA = string.Empty;
        private string GITHUB_WORKSPACE = string.Empty;
        private CheckRun? CurrentCheckRun = null;

#if ENABLE_GH_API
        private IGitHubClient? _gitHubClient = null;
        private IGitHubClient GitHubClient {
            get => _gitHubClient ?? throw new NullReferenceException($"{nameof(GitHubClient)} not initialized");
            set => _gitHubClient = value;
        }
#endif

        /// <summary>Concatenate Dictionaries, keep the first KeyValuePair on duplicate Keys</summary>
        private static Dictionary<string, string> BuildVariables(params Dictionary<string, string>[] args) {
            var result = new Dictionary<string, string>();
            foreach (var arg in args) {
                result = result.Concat(arg.Where(x => !result.ContainsKey(x.Key))).ToDictionary(x => x.Key, x => x.Value);
            }
            return result;
        }

        private static (string filename, string methodname, string sourceline) StackFrameFromTrace(string stackTrace) {
            var s = new StackFrame();
            var stackLine = stackTrace.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).First();
            var regex = new System.Text.RegularExpressions.Regex(@"(\ *)at (?<method>.*) in (?<filename>.*):line (?<linenumber>\d+)");
            var match = regex.Match(stackTrace);
            return (match.Groups["filename"].Value, match.Groups["method"].Value, match.Groups["linenumber"].Value);
        }

        private string WorkspaceRelativePath(string fullPath) {
            if (!string.IsNullOrEmpty(GITHUB_WORKSPACE)) {
                return fullPath.Replace(GITHUB_WORKSPACE, "").TrimStart('\\', '/');
            } else {
                return fullPath;
            }
        }

        private static string FormatTimeSpan(TimeSpan timeSpan) {
            if (timeSpan.TotalDays >= 1) {
                return string.Format("{0:0.0000} {1}", timeSpan.TotalDays, "Days");
            } else if (timeSpan.TotalHours >= 1) {
                return string.Format("{0:0.0000} {1}", timeSpan.TotalHours, "Hours");
            } else if (timeSpan.TotalMinutes >= 1) {
                return string.Format("{0:0.0000} {1}", timeSpan.TotalMinutes, "Minutes");
            } else {
                return string.Format("{0:0.0000} {1}", timeSpan.TotalSeconds, "Seconds");
            }
        }

        private string GetFullInformation(TestResult result) {
            var Output = new System.Text.StringBuilder();

            if (!String.IsNullOrEmpty(result.ErrorMessage)) {
                Output.AppendLine("== Error Message ==");
                Output.AppendLine(result.ErrorMessage);
            }

            if (!String.IsNullOrEmpty(result.ErrorStackTrace)) {
                Output.AppendLine("== Stack Trace ==");
                Output.AppendLine(result.ErrorStackTrace);
            }

            var stdOutMessagesList = result.Messages.Where(msg => msg.Category.Equals(TestResultMessage.StandardOutCategory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stdOutMessagesList.Count > 0) {
                Output.AppendLine("== Standard Output Messages ==");
                foreach (TestResultMessage message in stdOutMessagesList) {
                    Output.AppendLine(message.Text);
                }
            }

            var stdErrMessagesList = result.Messages.Where(msg => msg.Category.Equals(TestResultMessage.StandardErrorCategory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stdErrMessagesList.Count > 0) {
                Output.AppendLine("== Standard Error Messages ==");
                foreach (TestResultMessage message in stdErrMessagesList) {
                    Output.AppendLine(message.Text);
                }
            }

            var dbgTrcMessagesList = result.Messages.Where(msg => msg.Category.Equals(TestResultMessage.DebugTraceCategory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dbgTrcMessagesList.Count > 0) {
                Output.AppendLine("== Debug Trace Messages ==");
                foreach (TestResultMessage message in dbgTrcMessagesList) {
                    Output.AppendLine(message.Text);
                }
            }

            var addnlInfoMessagesList = result.Messages.Where(msg => msg.Category.Equals(TestResultMessage.AdditionalInfoCategory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (addnlInfoMessagesList.Count > 0) {
                Output.AppendLine("== Additional Information Messages ==");
                foreach (TestResultMessage message in addnlInfoMessagesList) {
                    Output.AppendLine(message.Text);
                }
            }
            return Output.ToString();
        }

        public void Initialize(TestLoggerEvents events, string testRunDirectory) {
            Initialize(events, new Dictionary<string, string>() { { "TestRunDirectory", testRunDirectory } });
        }

        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters) {

            Console.WriteLine("Initialize1-w");
            Debugger.Launch();

            var environmentVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process).ToDictionary<string, string>();

            // Build Vars, parameters take precedence over environment variables
            Vars = BuildVariables(parameters, environmentVariables);

            // Keep some frequently used Vars 
            GITHUB_REPOSITORY_OWNER = Vars.GetValueOrDefault("GITHUB_REPOSITORY_OWNER", string.Empty);

            GITHUB_REPOSITORY_NAME = Vars.GetValueOrDefault("GITHUB_REPOSITORY", string.Empty).Split('/').Last();

            GITHUB_TOKEN = Vars.GetValueOrDefault("GITHUB_TOKEN", string.Empty);

            GITHUB_SHA = Vars.GetValueOrDefault("GITHUB_SHA", string.Empty);

            GITHUB_WORKSPACE = Vars.GetValueOrDefault("GITHUB_WORKSPACE", String.Empty);

            // connection to github api 
#if ENABLE_GH_API
            GitHubClient = new GitHubClient(new ProductHeaderValue(GITHUB_REPOSITORY_OWNER)) {
                Credentials = new Credentials(GITHUB_TOKEN)
            };
#endif
            foreach (var p in Vars) {
                Console.WriteLine("+++ variable: " + p.Key + "=" + p.Value);
            }

            // Raised when a test message is received.
            events.TestRunMessage += Events_TestRunMessage;

            // Raised when a test run starts.
            events.TestRunStart += async (s, e) => await Events_TestRunStart(s, e);


            // Raised when a test result is received.
            events.TestResult += async (s, e) => await Events_TestResult(s, e);

            // Raised when a test run is complete.
            events.TestRunComplete += async (s, e) => await Events_TestRunComplete(s, e);
        }


        /// <summary>Raised when a test message is received.</summary>
        private void Events_TestRunMessage(object sender, TestRunMessageEventArgs e) {
            Console.WriteLine($"*** Events_TestRunMessage *** {e.Level}: {e.Message}");

        }

        /// <summary>Raised when a test run starts.</summary> 
        private async Task Events_TestRunStart(object sender, TestRunStartEventArgs e) {
            Console.WriteLine($"*** Events_TestRunStart ***");

            Console.Write("attempt check run creation");

            var newCheckRun = new NewCheckRun(CheckRunName, GITHUB_SHA) {
                Output = new NewCheckRunOutput(CheckRunName, "Starting...") {
                    //Text = "NewCheckRunOutput.Text",
                },
                Status = CheckStatus.Queued,
            };
#if ENABLE_GH_API
            CurrentCheckRun = await GitHubClient.Check.Run.Create(GITHUB_REPOSITORY_OWNER, GITHUB_REPOSITORY_NAME, newCheckRun);
#endif
        }

        /// <summary>Raised when a test result is received.</summary>
        private async Task Events_TestResult(object sender, TestResultEventArgs e) {
            Console.WriteLine($"*** Events_TestResult *** {e.Result.TestCase.FullyQualifiedName}: {e.Result.ErrorMessage}-{e.Result.Outcome} ");

            if (e.Result.Outcome == TestOutcome.Passed || e.Result.Outcome == TestOutcome.None) {
                //don't annotate successfull tests
                return;
            }

            NewCheckRunAnnotation? newAnnotation = null;
            switch (e.Result.Outcome) {
                case TestOutcome.Failed:
                    var (filename, methodname, sourceline) = StackFrameFromTrace(e.Result.ErrorStackTrace);
                    newAnnotation = new NewCheckRunAnnotation(WorkspaceRelativePath(filename), int.Parse(sourceline), int.Parse(sourceline) + 5, CheckAnnotationLevel.Failure, e.Result.ErrorMessage) {
                        Title = e.Result.DisplayName,
                        RawDetails = GetFullInformation(e.Result)
                    };
                    break;
                case TestOutcome.Skipped: //CheckAnnotationLevel.Warning ?
                    break;
                case TestOutcome.NotFound: // CheckAnnotationLevel.Failure ?
                    break;
            }
            if (newAnnotation != null) {
                var check = new CheckRunUpdate() {
                    Output = new NewCheckRunOutput(CheckRunName, "Running...") {
                        Annotations = new List<NewCheckRunAnnotation>() { newAnnotation }
                    },
                    Status = CheckStatus.InProgress
                };
#if ENABLE_GH_API
                CurrentCheckRun = await GitHubClient.Check.Run.Update(GITHUB_REPOSITORY_OWNER, GITHUB_REPOSITORY_NAME, CurrentCheckRun?.Id ?? -1, check);
#endif
            }
        }

        /// <summary>Raised when a test run is complete.</summary>
        private async Task Events_TestRunComplete(object sender, TestRunCompleteEventArgs e) {
            CheckConclusion gh_conclusion = CheckConclusion.Neutral;

            var stringBuilder = new StringBuilder();
            if (e.TestRunStatistics.Stats.Any(stat => stat.Key == TestOutcome.Failed && stat.Value > 0)) {
                // failed
                gh_conclusion = CheckConclusion.Failure;
                stringBuilder.AppendLine(":red_circle: Test Run Failed.");
            } else {
                // passed
                gh_conclusion = CheckConclusion.Success;
                stringBuilder.AppendLine(":green_circle: Test Run Successful.");
            }
            if (e.IsAborted || e.IsCanceled) {
                gh_conclusion = CheckConclusion.Cancelled;
            }

            stringBuilder.AppendLine($"Total tests: {e.TestRunStatistics.ExecutedTests}");

            foreach (var stat in e.TestRunStatistics.Stats) {
                stringBuilder.AppendLine(" - " + stat.Key switch
                {
                    TestOutcome.None => ":question:",
                    TestOutcome.Passed => ":heavy_check_mark:",
                    TestOutcome.Failed => ":heavy_multiplication_x:",
                    TestOutcome.Skipped => ":large_orange_diamond:",
                    TestOutcome.NotFound => ":skull_and_crossbones:",
                    _ => ":skull:"
                } + " " + $"{stat.Key.ToString()}: {stat.Value}");
            }
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"Total time: {FormatTimeSpan(e.ElapsedTimeInRunningTests)}");

            var check = new CheckRunUpdate() {
                Output = new NewCheckRunOutput(CheckRunName, stringBuilder.ToString()) {

                },
                Status = CheckStatus.Completed,
                Conclusion = gh_conclusion


            };
#if ENABLE_GH_API
                CurrentCheckRun = await GitHubClient.Check.Run.Update(GITHUB_REPOSITORY_OWNER, GITHUB_REPOSITORY_NAME, CurrentCheckRun?.Id ?? -1, check);
#endif 


        }

    }
}
