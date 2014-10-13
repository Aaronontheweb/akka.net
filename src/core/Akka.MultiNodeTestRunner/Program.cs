using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Akka.MultiNodeTestRunner
{
    class Discovery : IMessageSink, IDisposable
    {
        public Dictionary<string, List<NodeTest>> Tests { get; set; }

        public Discovery()
        {
            Tests = new Dictionary<string, List<NodeTest>>();
            Finished = new ManualResetEvent(false);
        }

        public ManualResetEvent Finished { get; private set; }

        public IMessageSink NextSink { get; private set; }

        public bool OnMessage(IMessageSinkMessage message)
        {
            var testCaseDiscoveryMessage = message as ITestCaseDiscoveryMessage;
            if (testCaseDiscoveryMessage != null)
            {
                var details = GetTestDetails(testCaseDiscoveryMessage);
                List<NodeTest> tests;
                if (Tests.TryGetValue(details.TestName, out tests))
                {
                    tests.Add(details);
                }
                else
                {
                    tests = new List<NodeTest>(new[] { details });
                }
                Tests[details.TestName] = tests;
            }

            if (message is IDiscoveryCompleteMessage)
                Finished.Set();

            return true;
        }

        private NodeTest GetTestDetails(ITestCaseDiscoveryMessage nodeTest)
        {
            var matches = Regex.Match(nodeTest.TestClass.Class.Name, "(.+)([0-9]+)");

            return new NodeTest
            {
                Node = Convert.ToInt32(matches.Groups[2].Value),
                TestName = matches.Groups[1].Value,
                TypeName = nodeTest.TestClass.Class.Name,
                MethodName = nodeTest.TestCase.TestMethod.Method.Name
            };
        }

        public void Dispose()
        {
            Finished.Dispose();
        }
    }

    class NodeTest
    {
        public int Node { get; set; }
        public string TestName { get; set; }
        public string TypeName { get; set; }
        public string MethodName { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            const string assemblyName = "ClassLibrary1.dll";

            using (var controller = new XunitFrontController(assemblyName))
            {
                using (var discovery = new Discovery())
                {
                    controller.Find(false, discovery, new TestFrameworkOptions());
                    discovery.Finished.WaitOne();

                    foreach (var test in discovery.Tests)
                    {
                        var processes = new List<Process>();

                        foreach (var nodeTest in test.Value)
                        {
                            //Loop through each test, work out number of nodes to run on and kick off process
                            var process = new Process();
                            processes.Add(process);
                            process.StartInfo.UseShellExecute = false;
                            //process.StartInfo.RedirectStandardOutput = true;
                            process.StartInfo.FileName = "Akka.NodeTestRunner.exe";
                            process.StartInfo.Arguments = String.Format(@"""{0}"" ""{1}"" ""{2}"" ""{3}""", nodeTest.Node,
                                assemblyName, nodeTest.TypeName, nodeTest.MethodName);
                            //process.StartInfo.CreateNoWindow = true;
                            var nodeIndex = nodeTest.Node;
                            process.OutputDataReceived +=
                                (sender, line) => Console.WriteLine("[Node{0}]{1}", nodeIndex, line.Data);
                            process.Start();
                            //process.BeginOutputReadLine();
                        }

                        foreach (var process in processes)
                        {
                            process.WaitForExit();
                            process.Close();
                        }
                    }
                }
            }



        }
    }
}
