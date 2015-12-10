using Akka.IO;
using Akka.MultiNodeTestRunner.Shared.Logging;
using Akka.Serialization;
using Akka.TestKit;
using Xunit;

namespace Akka.MultiNodeTestRunner.Shared.Tests.Logging
{
    /// <summary>
    /// Ensures that we can send <see cref="ByteString"/> objects that contain full
    /// .NET types over the network when collecting data from multiple nodes in a given test
    /// </summary>
    public class ByteStringSerializationTests : AkkaSpec
    {
        private Serializer InternalSerializer => Sys.Serialization.FindSerializerFor(typeof (string));

        private ByteStringSerializer _serializer;

        protected ByteStringSerializer Serializer => _serializer ?? (_serializer = new ByteStringSerializer(InternalSerializer));

        [Fact]
        public void Should_serialize_POCO_in_ByteString()
        {
            var testData = "SomeTest";
            var serialized = Serializer.ToByteString(testData);
            var deserialized = Serializer.FromByteString(serialized) as string;
            Assert.NotNull(deserialized);
            Assert.Equal(testData, deserialized);
        }
    }
}
