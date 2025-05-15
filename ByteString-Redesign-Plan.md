# ByteString Redesign Plan

## Goals
1. Make APIs zero-copy where possible
2. Support non-contiguous memory regions without unnecessary copying
3. Improve developer experience (DX) for buffer aggregation
4. Add proper support for Span<byte> and Memory<byte>
5. Maintain backward compatibility

## 1. Architectural Changes

### Current Architecture
- Uses `ArraySegment<byte>` (aliased as `ByteBuffer`) for internal data storage
- Stores segments in an array of `ByteBuffer[]`
- Many operations result in unnecessary copying
- Limited support for modern memory types

### New Architecture
- Replace `ArraySegment<byte>` with a more flexible abstraction that can wrap various memory sources
- Create a memory segment abstraction that can represent: `ArraySegment<byte>`, `Memory<byte>`, `ReadOnlyMemory<byte>`
- Implement non-copying view operations through these abstractions
- Enable efficient slicing without creating new arrays

## 2. Core Implementation

### Memory Segment Abstraction
```csharp
public interface IMemorySegment
{
    int Length { get; }
    byte this[int index] { get; }
    ReadOnlySpan<byte> AsSpan();
    void CopyTo(Span<byte> destination);
    IMemorySegment Slice(int start, int length);
    bool TryGetReadOnlyMemory(out ReadOnlyMemory<byte> memory);
}
```

### Segment Implementations
- `ArrayMemorySegment`: Wraps `ArraySegment<byte>` for backward compatibility
- `ReadOnlyMemorySegment`: Wraps `ReadOnlyMemory<byte>` for efficient memory sharing
- `EmptySegment`: Singleton implementation for empty segments

### ByteString Internal Structure
- Replace `ByteBuffer[] _buffers` with `List<IMemorySegment> _segments`
- Optimize for common cases (empty, single segment, multiple segments)

## 3. API Improvements

### New APIs
- `TryGetSingleSegment(out ReadOnlyMemory<byte> memory)` - For optimized access when data is contiguous
- `CopyToSequence(IBufferWriter<byte> writer)` - For integration with System.IO.Pipelines
- `AsReadOnlySequence<byte>()` - For efficient pipe operations
- Non-copying creation methods:
  - `FromMemory(ReadOnlyMemory<byte> memory)`
  - `FromSpan(ReadOnlySpan<byte> span)` (will copy to array but with proper allocation)

### API Enhancements
- Make existing methods use modern types internally
- Add span-based overloads for existing methods
- Support for efficient concatenation and slicing

## 4. Performance Optimizations

### Lazy Slicing
- Implement slicing as view operations that don't allocate new buffer arrays
- Track segment offsets and lengths rather than copying the data

### Concatenation Improvements
- Avoid creating large buffer arrays when concatenating multiple ByteStrings
- Reuse segment references where possible

### Memory Efficiency
- Implement pooling for internal array allocations
- Add specialized paths for small buffers
- Optimize Equals and GetHashCode operations

## 5. Implementation Strategy

### Phase 1: Core Abstraction
1. Create the `IMemorySegment` interface and implementations
2. Add internal methods to convert existing ByteString to use the new abstraction
3. Implement tests for the new abstractions

### Phase 2: ByteString Refactoring
1. Refactor ByteString to use the new segment abstraction internally
2. Update existing methods to use the new abstraction
3. Ensure backward compatibility

### Phase 3: New APIs and Optimizations
1. Add new APIs for zero-copy operations
2. Implement optimizations for key scenarios
3. Add benchmarks for the new implementation

### Phase 4: Testing and Finalization
1. Update existing tests to work with the new implementation
2. Add new tests for the new functionality
3. Benchmark old vs new implementation
4. Document new APIs and features