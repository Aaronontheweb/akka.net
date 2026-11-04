//-----------------------------------------------------------------------
// <copyright file="PersistAllAsyncCompletionOrderingSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using FluentAssertions;
using Xunit;

namespace Akka.Persistence.Tests
{
    /// <summary>
    /// Reproduces a message-ordering violation: commands that arrive while a
    /// <c>PersistAll(events, handler, Func&lt;Task&gt; onCompleteAsync)</c> is in flight must be
    /// handled afterwards in the order they were sent, and a message sent after them must be
    /// handled after all of them.
    /// </summary>
    public class PersistAllAsyncCompletionOrderingSpec : PersistenceSpec
    {
        private const int CommandCount = 20;

        private readonly ITestOutputHelper _output;

        public PersistAllAsyncCompletionOrderingSpec(ITestOutputHelper output)
            : base(Configuration("PersistAllAsyncCompletionOrderingSpec", serialization: "off"), output)
        {
            _output = output;
        }

        private sealed record Start(IActorRef? NotifyCompletionStarted = null);

        private sealed record Cmd(int N);

        private sealed record Marker;

        private sealed record Ping;

        private sealed record Evt(string Data);

        private enum CommandMode
        {
            /// <summary>Reply directly from the command handler.</summary>
            Reply,
            /// <summary>Persist one event per command and reply from its (sync) handler.</summary>
            Persist,
            /// <summary>PersistAll one event per command with an async completion that replies.</summary>
            PersistAllWithAsyncCompletion
        }

        private sealed class AsyncCompletionActor : ReceivePersistentActor
        {
            public override string PersistenceId { get; }

            public AsyncCompletionActor(string persistenceId, CommandMode mode, TimeSpan completionDelay, bool syncStartCompletion)
            {
                PersistenceId = persistenceId;

                Recover<Evt>(_ => { });

                Command<Ping>(_ => Sender.Tell(new Ping()));

                Command<Start>(start =>
                {
                    async Task OnCompleteAsync()
                    {
                        start.NotifyCompletionStarted?.Tell("completion-started");
                        await Task.Delay(completionDelay);
                    }

                    var events = new[] { new Evt("E1"), new Evt("E2") };
                    if (syncStartCompletion)
                        PersistAll(events, _ => { }, () => { });
                    else
                        PersistAll(events, _ => { }, OnCompleteAsync);
                });

                Command<Cmd>(cmd =>
                {
                    var sender = Sender;
                    switch (mode)
                    {
                        case CommandMode.Reply:
                            sender.Tell(cmd.N);
                            break;
                        case CommandMode.Persist:
                            Persist(new Evt($"C{cmd.N}"), _ => sender.Tell(cmd.N));
                            break;
                        case CommandMode.PersistAllWithAsyncCompletion:
                            async Task ReplyAsync()
                            {
                                // must genuinely go async; Task.Yield runs inline on the ActorTaskScheduler
                                await Task.Delay(10);
                                sender.Tell(cmd.N);
                            }

                            PersistAll(new[] { new Evt($"C{cmd.N}") }, _ => { }, ReplyAsync);
                            break;
                    }
                });

                Command<Marker>(_ => Sender.Tell("marker"));
            }
        }

        private IActorRef CreateActor(CommandMode mode, TimeSpan completionDelay, bool syncStartCompletion = false)
        {
            var actor = Sys.ActorOf(Props.Create(() =>
                new AsyncCompletionActor(Name, mode, completionDelay, syncStartCompletion)));

            // make sure recovery has completed so nothing below is stashed by recovery
            actor.Tell(new Ping());
            ExpectMsg<Ping>();
            return actor;
        }

        private async Task<object[]> CollectRepliesAsync()
        {
            var replies = new List<object>();
            for (var i = 0; i < CommandCount + 1; i++)
                replies.Add(await ExpectMsgAsync<object>(TimeSpan.FromSeconds(10)));

            _output.WriteLine("Observed order: " + string.Join(", ", replies));
            return replies.ToArray();
        }

        private static object[] ExpectedOrder() =>
            Enumerable.Range(1, CommandCount).Cast<object>().Append("marker").ToArray();

        private void SendCommandsAndMarker(IActorRef actor)
        {
            for (var i = 1; i <= CommandCount; i++)
                actor.Tell(new Cmd(i));
            actor.Tell(new Marker());
        }

        /// <summary>
        /// Commands and the marker are enqueued right behind <see cref="Start"/>, so they are
        /// stashed internally while the PersistAll write is in flight.
        /// </summary>
        [Fact(DisplayName = "Commands stashed during PersistAll with async completion must be handled in send order")]
        public async Task Should_preserve_order_When_commands_stashed_during_PersistAll_with_async_completion()
        {
            var actor = CreateActor(CommandMode.Reply, TimeSpan.FromMilliseconds(50));

            actor.Tell(new Start());
            SendCommandsAndMarker(actor);

            var replies = await CollectRepliesAsync();
            replies.Should().Equal(ExpectedOrder());
        }

        /// <summary>
        /// Commands are sent only after the async completion callback has started, i.e. while the
        /// mailbox is suspended by RunTask rather than while the actor is stashing.
        /// </summary>
        [Fact(DisplayName = "Commands sent while the async completion is running must be handled in send order")]
        public async Task Should_preserve_order_When_commands_sent_while_async_completion_running()
        {
            var actor = CreateActor(CommandMode.Reply, TimeSpan.FromMilliseconds(300));

            actor.Tell(new Start(TestActor));
            await ExpectMsgAsync("completion-started");
            SendCommandsAndMarker(actor);

            var replies = await CollectRepliesAsync();
            replies.Should().Equal(ExpectedOrder());
        }

        /// <summary>
        /// Same as the stashed case, but each command itself calls Persist, so the actor goes back
        /// into the persisting state (and internal stashing) for every command.
        /// </summary>
        [Fact(DisplayName = "Persisting commands stashed during PersistAll with async completion must be handled in send order")]
        public async Task Should_preserve_order_When_stashed_commands_also_persist()
        {
            var actor = CreateActor(CommandMode.Persist, TimeSpan.FromMilliseconds(50));

            actor.Tell(new Start());
            SendCommandsAndMarker(actor);

            var replies = await CollectRepliesAsync();
            replies.Should().Equal(ExpectedOrder());
        }
        /// <summary>
        /// Each stashed command itself uses PersistAll with an async completion, so every command
        /// re-triggers the double unstash.
        /// </summary>
        [Fact(DisplayName = "Commands that each PersistAll with async completion must be handled in send order")]
        public async Task Should_preserve_order_When_stashed_commands_also_PersistAll_with_async_completion()
        {
            var actor = CreateActor(CommandMode.PersistAllWithAsyncCompletion, TimeSpan.FromMilliseconds(50));

            actor.Tell(new Start());
            SendCommandsAndMarker(actor);

            var replies = await CollectRepliesAsync();
            replies.Should().Equal(ExpectedOrder());
        }

        /// <summary>
        /// Control: the same scenario with the synchronous <c>Action onComplete</c> overload.
        /// </summary>
        [Fact(DisplayName = "Control: commands stashed during PersistAll with sync completion are handled in send order")]
        public async Task Should_preserve_order_When_commands_stashed_during_PersistAll_with_sync_completion()
        {
            var actor = CreateActor(CommandMode.Reply, TimeSpan.Zero, syncStartCompletion: true);

            actor.Tell(new Start());
            SendCommandsAndMarker(actor);

            var replies = await CollectRepliesAsync();
            replies.Should().Equal(ExpectedOrder());
        }

    }
}
