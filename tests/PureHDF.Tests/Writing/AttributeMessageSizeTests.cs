using Xunit;

namespace PureHDF.Tests.Writing;

/// <summary>
/// An attribute message declares its own size in two bytes, so 65535 is the most it can be - and the data
/// shares that budget with the name, the datatype and the dataspace.
/// </summary>
/// <remarks>
/// A size beyond that used to wrap silently: the object header was allocated from the wrapped number and
/// then written at full length, which left the whole object unreadable rather than just the attribute. The
/// window was narrow - a little over 20 bytes wide, moving with the length of the attribute name.
/// </remarks>
public class AttributeMessageSizeTests
{
    /// <summary>
    /// The largest numeric attribute that still fits, pinning the limit from below: the message may use all
    /// 65535 bytes, so a budget counted too conservatively is as wrong as one counted too loosely.
    /// </summary>
    /// <remarks>
    /// 16375 ints is 65500 bytes of data, and the 35 bytes left over are the name, the datatype, the
    /// dataspace and the message's own fixed fields. One element more does not fit.
    /// </remarks>
    [Fact]
    public void TheLargestNumericAttributeThatFitsStillWrites()
    {
        // Arrange
        var expected = Enumerable.Range(0, 16_375).ToArray();

        var file = new H5File();
        file.Attributes["a"] = expected;

        var stream = new MemoryStream();

        // Act
        file.Write(stream);
        stream.Seek(0, SeekOrigin.Begin);

        // Assert
        using var read = H5File.Open(stream);
        Assert.Equal(expected, read.Attribute("a").Read<int[]>());
    }

    /// <summary>
    /// Nothing about the limit is specific to strings, and a non-string attribute has no variable-length
    /// form to fall back to - so it reports the limit rather than writing a file that cannot be read.
    /// </summary>
    /// <remarks>
    /// 16376 is the first count that does not fit; 16384 is 65536 bytes of data exactly, which passed the
    /// old <c>&gt; 64 * 1024</c> comparison and then became 0 as a ushort.
    /// </remarks>
    [Theory]
    [InlineData(16_376)]
    [InlineData(16_378)]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public void ANumericAttributeTooLargeThrows(int count)
    {
        // Arrange
        var file = new H5File();
        file.Attributes["a"] = Enumerable.Range(0, count).ToArray();

        // Act
        void action() => file.Write(new MemoryStream());

        // Assert
        var exception = Assert.Throws<Exception>(action);
        Assert.Contains("object header message can declare", exception.Message);
    }
}
