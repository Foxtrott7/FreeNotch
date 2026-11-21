using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WindowsNotch.Services;
using Xunit;

namespace WindowsNotch.Tests
{
    public sealed class LocalizationParityTests
    {
        private static Dictionary<string, string> LoadEmbeddedLang(string code)
        {
            var asm = typeof(LocalizationService).Assembly;
            string resName = $"WindowsNotch.Resources.Lang.{code}.json";
            using Stream? stream = asm.GetManifestResourceStream(resName);
            Assert.NotNull(stream);

            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream!);
            Assert.NotNull(dict);
            return dict!;
        }

        [Fact]
        public void GermanAndEnglishDictionaries_HaveIdenticalKeysAndNonEmptyValues()
        {
            var de = LoadEmbeddedLang("de");
            var en = LoadEmbeddedLang("en");

            Assert.NotEmpty(de);
            Assert.NotEmpty(en);

            var missingInEn = de.Keys.Except(en.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();
            var missingInDe = en.Keys.Except(de.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();

            Assert.True(missingInEn.Count == 0, $"Fehlende Keys in en.json: {string.Join(", ", missingInEn)}");
            Assert.True(missingInDe.Count == 0, $"Fehlende Keys in de.json: {string.Join(", ", missingInDe)}");

            var emptyDe = de.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
            var emptyEn = en.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();

            Assert.True(emptyDe.Count == 0, $"Leere Werte in de.json: {string.Join(", ", emptyDe)}");
            Assert.True(emptyEn.Count == 0, $"Leere Werte in en.json: {string.Join(", ", emptyEn)}");
        }

        [Theory]
        [InlineData(null, "de")]
        [InlineData("", "de")]
        [InlineData("fr", "de")]
        [InlineData("de", "de")]
        [InlineData("de-DE", "de")]
        [InlineData("en", "en")]
        [InlineData("EN-us", "en")]
        public void NormalizeCode_RestrictsToGermanAndEnglish(string? input, string expected)
        {
            Assert.Equal(expected, LocalizationService.NormalizeCode(input));
        }

        [Fact]
        public void SupportedLanguages_ContainsOnlyGermanAndEnglish_WithReadableNames()
        {
            Assert.Collection(
                LocalizationService.SupportedLanguages,
                first =>
                {
                    Assert.Equal("de", first.Code);
                    Assert.Equal("Deutsch", first.DisplayName);
                    Assert.Equal("Deutsch", first.ToString());
                },
                second =>
                {
                    Assert.Equal("en", second.Code);
                    Assert.Equal("English", second.DisplayName);
                    Assert.Equal("English", second.ToString());
                });
        }
    }
}
