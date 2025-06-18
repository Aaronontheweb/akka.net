# Fix for Akka.Streams AsyncEnumerable Disposal Issue #7381

## Problem Description

Issue #7381 reported a `System.NotSupportedException` when disposing stage with materialized `IAsyncEnumerable` in Akka.Streams. This was a variation of the earlier issue #6280, where the disposal of `IAsyncEnumerator<T>` in the `AsyncEnumerable<T>.Logic.PostStop()` method could throw exceptions if the underlying enumerator didn't properly support async disposal.

## Root Cause

The issue was in the `PostStop()` method of the `AsyncEnumerable<T>.Logic` class in `src/core/Akka.Streams/Implementation/Fusing/Ops.cs`. The method attempted to dispose the `IAsyncEnumerator<T>` asynchronously using a fire-and-forget pattern, but:

1. Some `IAsyncEnumerator<T>` implementations don't properly support `DisposeAsync()`
2. The code didn't handle `NotSupportedException` that could be thrown
3. There was no fallback to synchronous disposal when async disposal failed
4. No null check was performed before attempting disposal

## Solution

The fix improves the disposal pattern by:

1. **Adding null check**: Before attempting disposal, verify the enumerator exists
2. **Exception handling**: Catch `NotSupportedException` specifically 
3. **Fallback mechanism**: When async disposal fails with `NotSupportedException`, attempt synchronous disposal via `IDisposable.Dispose()` if available
4. **Better logging**: More descriptive logging for different failure scenarios

## Changes Made

### Code Changes

1. **Updated `AsyncEnumerable<T>.Logic.PostStop()` method** in `src/core/Akka.Streams/Implementation/Fusing/Ops.cs`:
   - Added null check for `_enumerator` before disposal
   - Added specific `NotSupportedException` handling
   - Added fallback to synchronous disposal
   - Improved error logging

### Test Changes

2. **Added new test case** in `src/core/Akka.Streams.Tests/Dsl/AsyncEnumerableSpec.cs`:
   - `AsyncEnumerableSource_BugFix7381_NotSupportedExceptionHandling()`: Tests the scenario where `DisposeAsync()` throws `NotSupportedException`
   - `NotSupportedDisposeAsyncEnumerable`: Helper class that simulates an enumerator that doesn't support async disposal

## Verification

The fix includes a comprehensive test that:
1. Creates an enumerator that throws `NotSupportedException` on `DisposeAsync()`
2. Verifies the stream handles the exception gracefully
3. Confirms that fallback to synchronous disposal occurs
4. Ensures no exceptions are propagated to the user

## Backward Compatibility

This fix is fully backward compatible and only improves the robustness of the disposal mechanism without changing any public APIs.

## Related Issues

- Fixes #7381: `System.NotSupportedException` when disposing stage with materialized `IAsyncEnumerable`
- Related to #6280: Earlier variation of the same disposal issue
- Related to #6903: AsyncEnumerable disposal on kill switch signal