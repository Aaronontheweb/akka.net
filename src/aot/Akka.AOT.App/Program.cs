// -----------------------------------------------------------------------
//  <copyright file="Program.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Akka.Actor;
using Akka.Actor.Setup;
using Akka.AOT.App.Actors;
using Akka.Event;

namespace Akka.AOT.App;

internal static class Program
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            // Run 1: the bare default - no HOCON at all.
            await RunAsync("aot", () => ActorSystem.Create("aot"));

            // Run 2: same thing, but through an (empty) BootstrapSetup, which is the path
            // Akka.Hosting and most DI integrations take.
            await RunAsync("aot-setup", () => ActorSystem.Create("aot-setup", ActorSystemSetup.Create(BootstrapSetup.Create())));

            Console.WriteLine("[canary] OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[canary] FAILED: {ex.GetType().FullName}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);

            var inner = ex.InnerException;
            var depth = 0;
            while (inner is not null && depth++ < 10)
            {
                Console.WriteLine($"[canary]  --> inner: {inner.GetType().FullName}: {inner.Message}");
                Console.WriteLine(inner.StackTrace);
                inner = inner.InnerException;
            }

            return 1;
        }
    }

    private static async Task RunAsync(string label, Func<ActorSystem> factory)
    {
        Console.WriteLine($"[canary] creating ActorSystem '{label}' ...");
        var system = factory();
        try
        {
            system.Log.Info("[canary] {0}: actor system up", label);

            var untyped = system.ActorOf(Props.Create(() => new AotUntypedActor()), "untyped-actor");
            var receive = system.ActorOf(Props.Create(() => new AotReceiveActor()), "receive-actor");

            var untypedReply = await untyped.Ask<string>($"hello untyped from {label}", AskTimeout);
            Console.WriteLine($"[canary] {label}: untyped replied '{untypedReply}'");

            var receiveReply = await receive.Ask<string>($"hello receive from {label}", AskTimeout);
            Console.WriteLine($"[canary] {label}: receive replied '{receiveReply}'");

            system.Log.Info("[canary] {0}: round-trip complete", label);
        }
        finally
        {
            await system.Terminate();
        }

        Console.WriteLine($"[canary] {label}: terminated");
    }
}
