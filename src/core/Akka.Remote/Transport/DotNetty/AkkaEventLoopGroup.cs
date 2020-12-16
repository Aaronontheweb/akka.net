using System;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Dispatch;
using DotNetty.Common.Concurrency;
using DotNetty.Transport.Channels;
using IRunnable = DotNetty.Common.Concurrency.IRunnable;

namespace Akka.Remote.Transport.DotNetty
{
    internal sealed class AkkaEventLoopGroup : IEventLoopGroup
    {
        private readonly ActorSystem _actorSystem;
        private readonly MessageDispatcher _dispatcher;

        public AkkaEventLoopGroup(ActorSystem actorSystem) : this(
            actorSystem.Dispatchers.Lookup(RARP.For(actorSystem).Provider.RemoteSettings.Dispatcher), actorSystem)
        {
        }

        /// <summary>
        ///     For testing purposes
        /// </summary>
        /// <param name="dispatcher"></param>
        /// <param name="actorSystem"></param>
        public AkkaEventLoopGroup(MessageDispatcher dispatcher, ActorSystem actorSystem)
        {
            _dispatcher = dispatcher;
            _actorSystem = actorSystem;
        }

        public void Execute(IRunnable task)
        {
            InternalExecute(new AdaptedRunnable(task));
        }

        public void Execute(Action<object> action, object state)
        {
            InternalExecute(new ActionWithStateRunnable(action, state));
        }

        public void Execute(Action action)
        {
            InternalExecute(new ActionRunnable(action));
        }

        public void Execute(Action<object, object> action, object context, object state)
        {
            InternalExecute(new ActionWithContextRunnable(context, state, action));
        }

        public Task<T> SubmitAsync<T>(Func<T> func)
        {
            return SubmitAsync(func, CancellationToken.None);
        }

        public Task<T> SubmitAsync<T>(Func<T> func, CancellationToken cancellationToken)
        {
            var node = new FuncSubmitQueueNode<T>(func, cancellationToken);
            InternalExecute(node);
            return node.Completion;
        }

        public Task<T> SubmitAsync<T>(Func<object, T> func, object state)
        {
            return SubmitAsync(func, state, CancellationToken.None);
        }

        public Task<T> SubmitAsync<T>(Func<object, T> func, object state, CancellationToken cancellationToken)
        {
            var node = new StateFuncSubmitQueueNode<T>(func, state, cancellationToken);
            InternalExecute(node);
            return node.Completion;
        }

        public Task<T> SubmitAsync<T>(Func<object, object, T> func, object context, object state)
        {
            return SubmitAsync(func, context, state, CancellationToken.None);
        }

        public Task<T> SubmitAsync<T>(Func<object, object, T> func, object context, object state,
            CancellationToken cancellationToken)
        {
            var node = new StateFuncWithContextSubmitQueueNode<T>(func, context, state, cancellationToken);
            InternalExecute(node);
            return node.Completion;
        }

        public bool IsShutdown { get; }
        public bool IsTerminated { get; }

        public IScheduledTask Schedule(IRunnable action, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public IScheduledTask Schedule(Action action, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public IScheduledTask Schedule(Action<object> action, object state, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public IScheduledTask Schedule(Action<object, object> action, object context, object state, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public Task ScheduleAsync(Action<object> action, object state, TimeSpan delay,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task ScheduleAsync(Action<object> action, object state, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public Task ScheduleAsync(Action action, TimeSpan delay, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task ScheduleAsync(Action action, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public Task ScheduleAsync(Action<object, object> action, object context, object state, TimeSpan delay)
        {
            throw new NotImplementedException();
        }

        public Task ScheduleAsync(Action<object, object> action, object context, object state, TimeSpan delay,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task ShutdownGracefullyAsync()
        {
            throw new NotImplementedException();
        }

        public Task ShutdownGracefullyAsync(TimeSpan quietPeriod, TimeSpan timeout)
        {
            throw new NotImplementedException();
        }

        public IEventLoop GetNext()
        {
            throw new NotImplementedException();
        }

        public Task RegisterAsync(IChannel channel)
        {
            throw new NotImplementedException();
        }

        IEventExecutor IEventExecutorGroup.GetNext()
        {
            return GetNext();
        }

        public bool IsShuttingDown => _actorSystem.WhenTerminated.IsCompleted;
        public Task TerminationCompletion => _actorSystem.WhenTerminated;

        private void InternalExecute(Dispatch.IRunnable execute)
        {
            _dispatcher.Schedule(execute);
        }

        private readonly struct AdaptedRunnable : IRunnable, Dispatch.IRunnable
        {
            private readonly IRunnable _run;

            public AdaptedRunnable(IRunnable run)
            {
                _run = run;
            }

            void IRunnable.Run()
            {
                _run.Run();
            }

            void Dispatch.IRunnable.Run()
            {
                _run.Run();
            }
        }

        private sealed class ActionWithContextRunnable : Dispatch.IRunnable
        {
            private readonly Action<object, object> _actionWithState;
            private readonly object _context;
            private readonly object _state;

            public ActionWithContextRunnable(object context, object state, Action<object, object> actionWithState)
            {
                _context = context;
                _state = state;
                _actionWithState = actionWithState;
            }

            public void Run()
            {
                _actionWithState(_context, _state);
            }
        }

        abstract class FuncQueueNodeBase<T> : Dispatch.IRunnable
        {
            private readonly CancellationToken cancellationToken;
            private readonly TaskCompletionSource<T> promise;

            protected FuncQueueNodeBase(TaskCompletionSource<T> promise, CancellationToken cancellationToken)
            {
                this.promise = promise;
                this.cancellationToken = cancellationToken;
            }

            public Task<T> Completion => promise.Task;

            public void Run()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    promise.TrySetCanceled();
                    return;
                }

                try
                {
                    var result = Call();
                    promise.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // todo: handle fatal
                    promise.TrySetException(ex);
                }
            }

            protected abstract T Call();
        }

        private sealed class FuncSubmitQueueNode<T> : FuncQueueNodeBase<T>
        {
            private readonly Func<T> func;

            public FuncSubmitQueueNode(Func<T> func, CancellationToken cancellationToken)
                : base(new TaskCompletionSource<T>(), cancellationToken)
            {
                this.func = func;
            }

            protected override T Call()
            {
                return func();
            }
        }

        private sealed class StateFuncSubmitQueueNode<T> : FuncQueueNodeBase<T>
        {
            private readonly Func<object, T> func;

            public StateFuncSubmitQueueNode(Func<object, T> func, object state, CancellationToken cancellationToken)
                : base(new TaskCompletionSource<T>(state), cancellationToken)
            {
                this.func = func;
            }

            protected override T Call()
            {
                return func(Completion.AsyncState);
            }
        }

        private sealed class StateFuncWithContextSubmitQueueNode<T> : FuncQueueNodeBase<T>
        {
            private readonly object context;
            private readonly Func<object, object, T> func;

            public StateFuncWithContextSubmitQueueNode(
                Func<object, object, T> func,
                object context,
                object state,
                CancellationToken cancellationToken)
                : base(new TaskCompletionSource<T>(state), cancellationToken)
            {
                this.func = func;
                this.context = context;
            }

            protected override T Call()
            {
                return func(context, Completion.AsyncState);
            }
        }
    }
}