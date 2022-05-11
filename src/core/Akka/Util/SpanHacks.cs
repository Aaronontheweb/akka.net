using System;
using System.Collections.Generic;
using System.Text;

namespace Akka.Util
{
    /// <summary>
    /// INTERNAL API.
    ///
    /// <see cref="Span{T}"/> polyfills that should be deleted once we drop .NET Standard 2.0 support.
    /// </summary>
    internal static class SpanHacks
    {
        /// <summary>
        /// Computes how long the string representation of an integer without allocations.
        /// </summary>
        /// <param name="i">The integer to evaluate.</param>
        /// <returns>The length, expressed as an integer.</returns>
        public static int ComputeStrLength(int i)
        {
            var mask = i >> 31;
            var abs = (i + mask) ^ mask;  // absolute value
            var sigfigs = 0;
            do{
                sigfigs++;
                abs /= 10;
            }while(abs != 0);

            if (i < 0)
                return sigfigs + 1;
            return sigfigs;
        }

        /// <summary>
        /// Writes an integer into a <see cref="Span{T}"/> without allocating on the heap.
        /// </summary>
        /// <param name="span">A span that is long enough to support the integer we're about to write into it.</param>
        /// <param name="i">The integer we're going to write</param>
        /// <param name="index">Optional. The start position in <see cref="span"/> we're going to begin writing in.</param>
        /// <returns>The number of bytes written to <see cref="span"/>.</returns>
        /// <remarks>
        /// Will not work for <see cref="Int32.MinValue"/>.
        /// </remarks>
        public static int WriteInt(Span<char> span, int i, int index = 0)
        {
            // 11 is the max characters for a span
            Span<char> tempSpan = stackalloc char[11];

            // need to cache initial start position to determine number of inserted bytes
            var position = 0;
            var negative = false;
            if (i < 0)
            {
                negative = true;
                i = -i;
            }

            do
            {
                tempSpan[position++] = (char)((i % 10) + '0');
                i /= 10;
            }while(i != 0);
		
            if(negative){
                tempSpan[position++] = '-';
            }
		
            
            // trim the fat
            var finalSpan = tempSpan.Slice(0, position);
            
            // have to reverse the bytes in order to get them into string format...
            finalSpan.Reverse();

            finalSpan.CopyTo(span.Slice(index));
            return position;
        }
        
        public static bool IsNumeric(char x)
        {
            return (x >= '0' && x <= '9');
        }

        /// <summary>
        /// Parses an integer from a string.
        /// </summary>
        /// <remarks>
        /// PERFORMS NO INPUT VALIDATION.
        /// </remarks>
        /// <param name="str">The span of input characters.</param>
        /// <returns>An <see cref="int"/>.</returns>
        public static int Parse(ReadOnlySpan<char> str)
        {
            if (TryParse(str, out var i))
                return i;
            throw new FormatException($"[{str.ToString()}] is now a valid numeric format");
        }

        /// <summary>
        /// Parses an integer from a string.
        /// </summary>
        /// <remarks>
        /// PERFORMS NO INPUT VALIDATION.
        /// </remarks>
        /// <param name="str">The span of input characters.</param>
        /// <param name="returnValue">The parsed integer, if any.</param>
        /// <returns>An <see cref="int"/>.</returns>
        public static bool TryParse(ReadOnlySpan<char> str, out int returnValue)
        {
            var pos = 0;
            returnValue = 0;
            var sign = 1;
            if (str[0] == '-')
            {
                sign = -1;
                pos++;
            }

            for (; pos < str.Length; pos++)
            {
                if (!IsNumeric(str[pos]))
                    return false;
                returnValue = returnValue * 10 + str[pos] - '0';
            }

            returnValue = sign * returnValue;

            return true;
        }

        /// <summary>
        /// Performs <see cref="string.ToLowerInvariant"/> without having to
        /// allocate a new <see cref="string"/> first.
        /// </summary>
        /// <param name="input">The set of characters to be lower-cased</param>
        /// <returns>A new string.</returns>
        public static string ToLowerInvariant(ReadOnlySpan<char> input)
        {
            Span<char> output = stackalloc char[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                output[i] = char.ToLowerInvariant(input[i]);
            }
            return output.ToString();
        }
    }
}
