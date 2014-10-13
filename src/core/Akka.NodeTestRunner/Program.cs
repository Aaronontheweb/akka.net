using System;
using System.Diagnostics;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Akka.NodeTestRunner
{
    class Sink : IMessageSink, IDisposable
    {
        public bool Passed { get; private set; }
        public ManualResetEvent Finished { get; private set; }
        readonly int _nodeIndex;

        public Sink(int nodeIndex)
        {
            _nodeIndex = nodeIndex;
            Finished = new ManualResetEvent(false);
        }

        public bool OnMessage(IMessageSinkMessage message)
        {
            var testPassed = message as ITestPassed;
            if (testPassed != null)
            {
                Console.WriteLine("[Node{0}] {1} passed.", _nodeIndex, testPassed.TestCase.DisplayName);
                Passed = true;
                return true;
            }

            var testFailed = message as ITestFailed;
            if (testFailed != null)
            {
                Console.WriteLine("[Node{0}] {1} failed.", _nodeIndex, testFailed.TestDisplayName);
                foreach(var failedMessage in testFailed.Messages) Console.WriteLine(failedMessage);
                return true;
            }

            if (message is ITestAssemblyFinished)
            {
                Finished.Set();
            }

            return true;
        }

        public void Dispose()
        {
            Finished.Dispose();
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var nodeIndex = Convert.ToInt32(args[0]);
            var assemblyName = args[1];
            var typeName = args[2];
            var testName = args[3];
            var displayName = "Whatever";

            using (var controller = new XunitFrontController(assemblyName))
            {
                using (var sink = new Sink(nodeIndex))
                {
                    controller.RunTests(new[] { new Xunit1TestCase(assemblyName, null, typeName, testName, displayName) }, sink, new TestFrameworkOptions());
                    sink.Finished.WaitOne();
                    Environment.Exit(sink.Passed ? 0 : 1);
                }
            }
        }


    }
}
