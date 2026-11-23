using System.Collections.Generic;
using WindowsNotch.Core;
using WindowsNotch.Modules.Finance;
using WindowsNotch.Services;
using Xunit;

namespace WindowsNotch.Tests
{
    public sealed class AppSettingsTests
    {
        [Fact]
        public void Normalize_PreservesCustomWeatherCities_AndDeduplicates()
        {
            var settings = new AppSettings
            {
                WeatherCity = "München",
                WeatherCities = new List<string> { "  München ", "Wien", "münchen", "" },
                WeatherCityIndex = 5
            };

            settings.Normalize();

            Assert.Equal(new[] { "München", "Wien" }, settings.WeatherCities);
            Assert.Equal(1, settings.WeatherCityIndex);
        }

        [Fact]
        public void Normalize_WrapsNegativeWeatherCityIndexCleanly()
        {
            var settings = new AppSettings
            {
                WeatherCities = new List<string> { "Köln", "Zürich" },
                WeatherCityIndex = -1
            };

            settings.Normalize();

            Assert.Equal(1, settings.WeatherCityIndex);
        }

        [Fact]
        public void Normalize_EnsuresFourBentoSlotsAndValidTabs()
        {
            var settings = new AppSettings
            {
                BentoTileOrder = new List<string> { "planner", "unknown_tile" },
                TopTabOrder = new List<string> { "widgets", "widgets", "not_a_module" }
            };

            settings.Normalize();

            Assert.Equal(4, settings.BentoTileOrder.Count);
            Assert.Equal("calendar", settings.BentoTileOrder[0]);
            Assert.DoesNotContain("unknown_tile", settings.BentoTileOrder);
            Assert.DoesNotContain("not_a_module", settings.TopTabOrder);
            Assert.Single(settings.TopTabOrder, id => id == "widgets");
        }

        [Fact]
        public void FinanceRange_NormalizeAndCycle_FollowsFullOrder()
        {
            var expected = new[] { "LIVE", "1D", "1W", "1M", "1Y", "5Y", "ALL" };
            string current = "LIVE";
            for (int i = 1; i <= expected.Length; i++)
            {
                current = FinanceService.GetNextRange(current);
                Assert.Equal(expected[i % expected.Length], current);
            }

            var settings = new AppSettings { FinanceRange = "5Y" };
            settings.Normalize();
            Assert.Equal("5Y", settings.FinanceRange);

            settings.FinanceRange = "INVALID";
            settings.Normalize();
            Assert.Equal("1D", settings.FinanceRange);
        }

        [Fact]
        public void WeatherCities_ParseFormatAndSet_PreservesCustomCitiesWithoutFallback()
        {
            var parsed = AppSettings.ParseWeatherCities(" Stuttgart ; Ulm , stuttgart , , ");
            Assert.Equal(new[] { "Stuttgart", "Ulm" }, parsed);
            Assert.Equal("Stuttgart, Ulm", AppSettings.FormatWeatherCities(parsed));

            var settings = new AppSettings
            {
                WeatherCity = "Berlin",
                WeatherCities = new List<string> { "Berlin", "München" },
                WeatherLatitude = 52.52,
                WeatherLongitude = 13.40,
                WeatherResolvedCity = "Berlin"
            };

            settings.SetWeatherCities(parsed);
            settings.Normalize();

            Assert.Equal(new[] { "Stuttgart", "Ulm" }, settings.WeatherCities);
            Assert.Equal("Stuttgart", settings.WeatherCity);
            Assert.Equal(0, settings.WeatherCityIndex);
            Assert.Null(settings.WeatherLatitude);
            Assert.Null(settings.WeatherLongitude);
            Assert.Equal(string.Empty, settings.WeatherResolvedCity);

            // Hinzufügen eines weiteren Orts behält den aktiven Ort bei
            settings.WeatherCity = "Ulm";
            settings.WeatherCityIndex = 1;
            settings.SetWeatherCities(AppSettings.ParseWeatherCities("Stuttgart, Ulm, Augsburg"));
            Assert.Equal(new[] { "Stuttgart", "Ulm", "Augsburg" }, settings.WeatherCities);
            Assert.Equal("Ulm", settings.WeatherCity);
            Assert.Equal(1, settings.WeatherCityIndex);
        }

        [Fact]
        public void WeatherCondition_DifferentiatesRainIntensityByPrecipitation()
        {
            Assert.Equal("Nieselregen", WeatherService.DescribeCondition(51, 0.2));
            Assert.Equal("Leichter Regen", WeatherService.DescribeCondition(61, 0.6));
            Assert.Equal("Regen", WeatherService.DescribeCondition(61, 2.4));
            Assert.Equal("Starker Regen", WeatherService.DescribeCondition(61, 6.0));
            Assert.Equal("Starker Regen", WeatherService.DescribeCondition(65, 0.5));
        }

        [Fact]
        public void Normalize_ForcesUnifiedOledDarkTheme()
        {
            foreach (var legacyTheme in new[] { NotchTheme.DarkGlass, NotchTheme.Light, NotchTheme.LightGlass })
            {
                var settings = new AppSettings { Theme = legacyTheme };
                settings.Normalize();
                Assert.Equal(NotchTheme.Dark, settings.Theme);
            }
        }

        [Fact]
        public void NotchInfo_PetEyesSlot_IsSanitizedAndPreserved()
        {
            var settings = new AppSettings();
            settings.CollapsedLayout[NotchInfo.PetEyes] = NotchSlot.Center;
            settings.HoverLayout[NotchInfo.PetEyes] = NotchSlot.Left;
            settings.Normalize();

            Assert.Equal(NotchSlot.Center, settings.CollapsedLayout[NotchInfo.PetEyes]);
            Assert.Equal(NotchSlot.Left, settings.HoverLayout[NotchInfo.PetEyes]);
        }
    }
}
