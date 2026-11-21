using System.IO;
using WindowsNotch.Modules.PhoneTransfer;
using WindowsNotch.Modules.ScreenOcr;
using Xunit;

namespace WindowsNotch.Tests
{
    public sealed class SecurityTests
    {
        [Theory]
        [InlineData("../../evil.txt", "evil.txt")]
        [InlineData(@"..\..\Windows\System32\cmd.exe", "cmd.exe")]
        [InlineData("report.pdf...", "report.pdf")]
        [InlineData("CON.txt", "_CON.txt")]
        [InlineData("nul", "_nul")]
        public void SanitizeUploadFileName_NeutralizesTraversalAndReservedNames(string raw, string expected)
        {
            Assert.Equal(expected, PhoneTransferService.SanitizeUploadFileName(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("...")]
        public void SanitizeUploadFileName_ReplacesEmptyOrDotNamesWithFallback(string? raw)
        {
            string result = PhoneTransferService.SanitizeUploadFileName(raw);
            Assert.StartsWith("upload_", result);
            Assert.EndsWith(".bin", result);
        }

        [Fact]
        public void IsPathInsideDirectory_PreventsDirectoryEscape()
        {
            string root = Path.Combine(Path.GetTempPath(), "WindowsNotchTestRoot");
            Assert.True(PhoneTransferService.IsPathInsideDirectory(Path.Combine(root, "photo.jpg"), root));
            Assert.False(PhoneTransferService.IsPathInsideDirectory(Path.Combine(root, "..", "escape.txt"), root));
            Assert.False(PhoneTransferService.IsPathInsideDirectory(root + "Sibling\\file.txt", root));
        }

        [Theory]
        [InlineData("https://example.com", true)]
        [InlineData("http://localhost:8080/test", true)]
        [InlineData("mailto:user@example.com", true)]
        [InlineData("tel:+49123456", true)]
        [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("ms-msdt:/id PCWDiagnostic", false)]
        [InlineData(@"\\attacker\share\evil.exe", false)]
        public void ScreenOcr_IsNavigableUri_AllowsOnlySafeSchemes(string uri, bool expected)
        {
            Assert.Equal(expected, ScreenOcrService.IsNavigableUri(uri));
        }
    }
}
