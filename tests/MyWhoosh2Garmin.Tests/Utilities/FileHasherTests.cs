using MyWhoosh2Garmin.Utilities;

namespace MyWhoosh2Garmin.Tests.Utilities;

public class FileHasherTests
{
    [Fact]
    public void ComputeSha256_returns_known_hash_for_known_content()
    {
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "hello world");

        try
        {
            string hash = FileHasher.ComputeSha256(tempFile);

            // sha256("hello world") — verified independently with `shasum -a 256`.
            Assert.Equal(
                "B94D27B9934D3E08A52E52D7DA7DABFAC484EFE37A5380EE9088F7ACE2EFCDE9",
                hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
