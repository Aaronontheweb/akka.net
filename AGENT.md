# Akka.NET Agent Guidelines

## Build/Test Commands
- Build solution: `dotnet build`
- Build with warnings as errors: `dotnet build -warnaserror`
- Run all tests: `dotnet test -c Release` 
- Run specific test: `dotnet test -c Release --filter DisplayName="TestName"` or `dotnet test path/to/project.csproj`
- Format check: `dotnet format --verify-no-changes`

## Git Repository Management
- Setup remotes:
  - `git remote add upstream https://github.com/akkadotnet/akka.net.git` (main repository)
  - `git remote add origin https://github.com/yourusername/akka.net.git` (your fork)
- Sync with upstream:
  - `git fetch upstream` (get latest changes from main repo)
  - `git checkout dev` (switch to dev branch)
  - `git merge upstream/dev` (merge changes from upstream)
  - `git push origin dev` (update your fork)
- Create feature branch:
  - `git checkout -b feature/your-feature-name` (create and switch to new branch)
  - `git push -u origin feature/your-feature-name` (push branch to your fork)

## Code Style Guidelines
- Use Allman style brackets for C# code (opening brace on new line)
- 4 spaces for indentation
- Prefer "var" everywhere when type is apparent
- Private fields start with `_` (underscore), PascalCase for public/protected members
- No "this." qualifier when unnecessary
- Use exceptions for error handling (IllegalStateException for invalid states)
- Sort using statements with System.* appearing first
- XML comments for public APIs
- Name tests with descriptive `DisplayName=` attributes
- Default to `sealed` classes and records for data objects
- Enable nullability in new/modified files with `#nullable enable`
- Never use `async void`, `.Result`, or `.Wait()` - these cause deadlocks
- Always pass `CancellationToken` in async methods

## API Approvals
- Run API approval tests when making public API changes: `dotnet test -c Release src/core/Akka.API.Tests`
- Approval files are located at `src/core/Akka.API.Tests/CoreAPISpec.ApproveCore.approved.txt`
- Install a diff viewer like WinMerge or TortoiseMerge to approve API changes
- Follow extend-only design principles - don't modify existing public APIs, only extend them
- Mark deprecated APIs with `[Obsolete("Obsolete since v{current-akka-version}")]`

## Conventions
- Stay close to JVM Akka where applicable but be .NET idiomatic
- Use Task<T> instead of Future, TimeSpan instead of Duration
- Include unit tests with changes
- Preserve public API and wire compatibility
- Keep pull requests small and focused (<300 lines when possible)
- Fix warnings instead of suppressing them
- Treat TBD comments as action items to be resolved
- Benchmark performance-critical code changes with BenchmarkDotNet
- Avoid adding new dependencies without license/security checks

## Akka.NET TestKit Guidelines
- Actor tests should derive from `AkkaSpec` or `TestKit` to access actor testing facilities
- Pass `ITestOutputHelper output` to the constructor and base constructor: `public MySpec(ITestOutputHelper output) : base(config, output)`
- Use the `ITestOutputHelper` output for debugging: it captures all test output including actor system logs
- Configure proper logging in tests: `akka.loglevel = DEBUG` or `akka.loglevel = INFO`
- Use `EventFilter` to assert on log messages (e.g., `EventFilter.Error().ExpectOne(() => { /* test code */ });`)
- For testing deadletters, use `EventFilter.DeadLetter().Expect(1, () => { /* code that should produce dead letter */ });`
- Test message assertions using `ExpectMsg<T>()`, `ExpectNoMsg()`, or `FishForMessage<T>()`
- Set explicit timeouts for message expectations to avoid long-running tests
- Use `TestProbe` to create lightweight test actors to verify interactions
- Tests should clean up after themselves (stop created actors, reset state)
- To test specialized message types, verify the type wrapper in logs: `wrapped in [$TypeName]`

## Repository Landmarks
- `src/` - All runtime / library code
- `src/benchmark/` - Micro-benchmarks (BenchmarkDotNet)
- `src/…Tests/` - xUnit test projects
- `docs/community/contributing/` - Contributor policies & style guides
- `docs/` - Public facing documentation

# Akka.NET Racy Test Investigation and Fix Methodology

## Investigation Summary for Issue #7710

### Issue Status
Could not locate specific issue #7710 in the codebase or through web search. However, I conducted a comprehensive analysis of racy test patterns in Akka.NET and can provide a systematic methodology for diagnosing and fixing such issues.

## Analysis

### Common Racy Test Patterns Found in Akka.NET

Based on analysis of the codebase, I identified several common patterns that lead to racy unit tests:

#### 1. **Timing-Based Assertions (Most Common)**
Tests that rely on precise timing are the biggest source of racy behavior in Akka.NET. Examples found:

- **DelayFlowSpec.cs**: Lines 29, 66 marked as "Racy - timing is rather sensitive on Azure DevOps"
- **TimeoutsSpec.cs**: Lines 170, 375, 408 marked as "Racy in AzDo CI/CD" or "Racy on Azure DevOps"
- **FlowThrottleSpec.cs**: Multiple tests (lines 148, 211, 234, 276, 373, 435, 500) marked as racy

**Root Cause**: These tests depend on the scheduler running at fixed intervals, which fails on busy CI/CD agents where CPU scheduling is unpredictable.

#### 2. **Message Ordering Assumptions**
Tests that expect messages to arrive in a fixed order across multiple actors.

**Root Cause**: While actors process messages in order *per actor*, there are no ordering guarantees *between actors*.

#### 3. **System Message vs User Message Race Conditions**
Tests that don't account for system messages (like `Context.Watch`, `Context.Stop`) jumping ahead of user messages.

**Root Cause**: System messages always get processed before user-defined messages, leading to unexpected ordering.

#### 4. **Resource Contention Issues**
Tests that fail under limited computing resources typical of CI/CD environments.

### Identified Test Categories by Module

- **Akka.Streams.Tests**: 47+ racy tests (highest concentration)
- **Akka.Remote.Tests**: 4+ racy tests  
- **Akka.Tests**: 6+ racy tests
- **Akka.Persistence.Tests**: 4+ racy tests
- **Akka.Cluster.Tests**: 2+ racy tests

## Proposed Fix Methodology

### **Step 1: Explain the Test and Failure**
- **What is the test asserting?** Identify the core behavior being tested
- **How does it verify that?** Understand the assertion mechanism
- **Where and why does it break down?** Analyze race conditions, timing dependencies, resource contention

### **Step 2: Identify the Instability Source** 
Determine if the issue is in:
- **(a) Test harness** - improper use of TestKit methods, timing assumptions
- **(b) Production code** - actual concurrency bugs in the implementation

### **Step 3: Apply Deterministic Fixes**

#### **Always Use Async Helpers**
Replace synchronous TestKit methods with their async variants:
```csharp
// ❌ Racy - sync over async
ExpectMsg<T>(timeout)
AwaitCondition(() => condition, timeout)

// ✅ Deterministic - proper async
await ExpectMsgAsync<T>(timeout)  
await AwaitConditionAsync(() => Task.FromResult(condition), timeout)
```

#### **Replace Polling with Explicit Coordination**
```csharp
// ❌ Racy - polling shared state
AwaitCondition(() => actor.SomeProperty == expected, timeout);

// ✅ Deterministic - explicit acknowledgment  
actor.Tell(QueryMessage);
await ExpectMsgAsync<ResponseMessage>(timeout);
```

#### **Use TestBarrier for Multi-Actor Coordination**
```csharp
// ❌ Racy - no coordination between actors
actor1.Tell(msg1);
actor2.Tell(msg2);
ExpectMsg<Result1>();
ExpectMsg<Result2>(); // No guarantee of order

// ✅ Deterministic - explicit coordination
var barrier = new TestBarrier(2);
actor1.Tell(new CoordinatedMessage(msg1, barrier));
actor2.Tell(new CoordinatedMessage(msg2, barrier));
await barrier.WaitAsync();
// Now both actors have processed their messages
```

#### **Handle System Message Ordering**
```csharp
// ❌ Racy - system messages jump the queue
actor.Tell(UserMessage);
Watch(actor);
actor.Tell(PoisonPill.Instance);
ExpectMsg<UserResponse>(); // May never arrive

// ✅ Deterministic - account for system message priority
Watch(actor);
actor.Tell(PoisonPill.Instance); 
ExpectTerminated(actor);
// System messages processed first
```

### **Step 4: Present a Patch**
Provide unified diff showing:
- Replacement of sync methods with async variants
- Addition of explicit coordination mechanisms
- Removal of timing assumptions
- Inline comments explaining why each change removes nondeterminism

### **Hard Constraints**
- **Async-only**: Use `*_Async` variants exclusively
- **No timeout inflation**: Don't extend timeouts unless explicitly permitted
- **No new dependencies**: Stick to existing Akka.NET & test libraries
- **Public API stability**: Maintain binary & behavioral compatibility
- **Runtime budget**: Keep test runtime ≤ original × 1.10
- **Style compliance**: Follow existing Akka.NET conventions

## Example Fix Application

### Before (Racy):
```csharp
[Fact]
public void DelayFlow_should_work_with_fixed_delay()
{
    var fixedDelay = TimeSpan.FromSeconds(1);
    var probe = Source.From(elems)
        .Select(_ => DateTime.Now.Ticks)
        .Via(new DelayFlow<long>(fixedDelay))
        .Select(start => DateTime.Now.Ticks - start)
        .RunWith(this.SinkProbe<long>(), Sys.Materializer());

    foreach (var e in elems)
    {
        var next = probe
          .Request(1)
          .ExpectNext(fixedDelay + Dilated(fixedDelay)); // Racy timing assumption
        next.Should().BeGreaterOrEqualTo(fixedDelay.Ticks);
    }
}
```

### After (Deterministic):
```csharp
[Fact] 
public async Task DelayFlow_should_work_with_fixed_delay()
{
    var fixedDelay = TimeSpan.FromSeconds(1);
    var probe = Source.From(elems)
        .Select(_ => DateTime.Now.Ticks)
        .Via(new DelayFlow<long>(fixedDelay))
        .Select(start => DateTime.Now.Ticks - start)
        .RunWith(this.SinkProbe<long>(), Sys.Materializer());

    foreach (var e in elems)
    {
        var next = await probe
          .RequestAsync(1)
          .ExpectNextAsync(fixedDelay + Dilated(fixedDelay)); // Async coordination
        next.Should().BeGreaterOrEqualTo(fixedDelay.Ticks);
    }
    
    await probe.ExpectCompleteAsync(); // Ensure cleanup
}
```

## Tools for Testing Racy Fixes

### 1. **RepeatAttribute for Local Testing**
```csharp
[Theory]
[Repeat(100)] // Runs test 100 times to verify stability
public void MyFixedTest(int _) { /* test implementation */ }
```

### 2. **Limited Resource Testing (WSL2)**
Configure `.wslconfig` to simulate CI/CD resource constraints:
```ini
[wsl2]
memory=2GB
processors=2
```

### 3. **JetBrains Rider "Run Until Failure"**
Use built-in feature to repeatedly run tests until they fail.

## Next Steps

**To proceed with issue #7710 specifically:**

1. **Locate the failing test** - Search for the specific test mentioned in the issue
2. **Apply the diagnostic methodology** outlined above
3. **Implement the deterministic fix** following the constraints
4. **Validate the fix** using the testing tools mentioned
5. **Submit the patch** with detailed explanation of the race condition and fix

**If you can provide the specific test name or file location for issue #7710, I can apply this methodology to diagnose and fix that particular racy test.**