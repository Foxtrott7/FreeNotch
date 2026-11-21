using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsNotch.Core;

namespace WindowsNotch.Services
{
    public sealed class WeatherForecastDay
    {
        public DateTime Date { get; set; }
        public int WeatherCode { get; set; }
        public double MinC { get; set; }
        public double MaxC { get; set; }
    }

    public sealed class WeatherSnapshot
    {
        public string CityName { get; set; } = string.Empty;
        public double TemperatureC { get; set; }
        public int WeatherCode { get; set; }
        public bool IsDay { get; set; } = true;
        public double PrecipitationMm { get; set; }
        public int CloudCoverPercent { get; set; }
        public double TodayMinC { get; set; }
        public double TodayMaxC { get; set; }
        public List<WeatherForecastDay> ForecastDays { get; set; } = new();
        public DateTime FetchedAtUtc { get; set; }

        public string ConditionText => WeatherService.DescribeCondition(WeatherCode, PrecipitationMm);
        public NotchIconKind IconKind => WeatherService.MapWmoToIcon(WeatherCode, IsDay);
    }

    public sealed class WeatherCitySearchResult
    {
        public string Name { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Admin1 { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string SubLabel => string.Join(", ", new[] { Admin1, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// Lädt lokale Wetterdaten über Open-Meteo (nur bei aktivem Opt-in in den Einstellungen).
    /// </summary>
    public sealed class WeatherService
    {
        private static readonly Lazy<WeatherService> _instance = new(() => new WeatherService());
        public static WeatherService Instance => _instance.Value;

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, WeatherSnapshot> _cityCache = new(StringComparer.OrdinalIgnoreCase);
        private string _lastQueryCity = string.Empty;

        public WeatherSnapshot? Current { get; private set; }
        public string? LastError { get; private set; }
        public bool IsLoading { get; private set; }

        public event Action? WeatherUpdated;
        public event Action? CitiesChanged;

        public IReadOnlyList<string> GetCities()
        {
            var s = SettingsService.Current;
            if (s.WeatherCities is { Count: > 0 })
                return s.WeatherCities;

            string primary = (s.WeatherCity ?? string.Empty).Trim();
            return string.IsNullOrEmpty(primary) ? Array.Empty<string>() : new[] { primary };
        }

        public string? GetActiveCity()
        {
            var cities = GetCities();
            if (cities.Count == 0) return null;
            int idx = ((SettingsService.Current.WeatherCityIndex % cities.Count) + cities.Count) % cities.Count;
            return cities[idx];
        }

        public bool IsFavoriteCity(string city)
            => !string.IsNullOrWhiteSpace(city) &&
               GetCities().Any(c => string.Equals(c, city.Trim(), StringComparison.OrdinalIgnoreCase));

        public bool ToggleCity(string city, double? lat = null, double? lon = null, string? resolvedName = null)
        {
            if (IsFavoriteCity(city))
            {
                RemoveCity(city);
                return false;
            }
            AddCity(city, lat, lon, resolvedName);
            return true;
        }

        public void SetActiveCity(string city)
        {
            var cities = GetCities().ToList();
            int idx = cities.FindIndex(c => string.Equals(c, city.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;

            SwitchToCityList(cities, idx);
        }

        public void CycleCity(int step = 1)
        {
            var cities = GetCities().ToList();
            if (cities.Count <= 1) return;

            int nextIdx = (((SettingsService.Current.WeatherCityIndex + step) % cities.Count) + cities.Count) % cities.Count;
            SwitchToCityList(cities, nextIdx);
        }

        private void SwitchToCityList(List<string> cities, int idx)
        {
            string targetCity = cities[idx];
            SettingsService.UpdateQuiet(s =>
            {
                s.WeatherCities = cities;
                s.WeatherCityIndex = idx;
                s.WeatherCity = targetCity;
                s.WeatherLatitude = null;
                s.WeatherLongitude = null;
                s.WeatherResolvedCity = string.Empty;
            });
            _lastQueryCity = string.Empty;
            Current = _cityCache.TryGetValue(targetCity, out var cached) ? cached : null;
            CitiesChanged?.Invoke();

            if (SettingsService.Current.OptInWeather)
            {
                bool stale = cached == null || DateTime.UtcNow - cached.FetchedAtUtc > TimeSpan.FromMinutes(15);
                if (stale)
                    _ = RefreshIfNeededAsync(force: true);
            }
        }

        public void AddCity(string city, double? lat = null, double? lon = null, string? resolvedName = null)
        {
            string clean = (city ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clean)) return;

            SettingsService.UpdateQuiet(s =>
            {
                var list = s.WeatherCities?.ToList() ?? new List<string>();
                if (list.Count == 0 && !string.IsNullOrWhiteSpace(s.WeatherCity))
                    list.Add(s.WeatherCity.Trim());

                int existing = list.FindIndex(c => string.Equals(c, clean, StringComparison.OrdinalIgnoreCase));
                if (existing < 0)
                {
                    list.Add(clean);
                    existing = list.Count - 1;
                }

                s.WeatherCities = list;
                s.WeatherCityIndex = existing;
                s.WeatherCity = list[existing];
                s.WeatherLatitude = lat;
                s.WeatherLongitude = lon;
                s.WeatherResolvedCity = resolvedName ?? (lat.HasValue ? clean : string.Empty);
            });
            _lastQueryCity = string.Empty;
            Current = _cityCache.TryGetValue(clean, out var cached) ? cached : null;
            CitiesChanged?.Invoke();
            if (SettingsService.Current.OptInWeather)
                _ = RefreshIfNeededAsync(force: true);
        }

        public void RemoveCity(string city)
        {
            string clean = (city ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(clean)) return;

            string? nextActive = null;
            SettingsService.UpdateQuiet(s =>
            {
                var list = s.WeatherCities?.ToList() ?? new List<string>();
                int idx = list.FindIndex(c => string.Equals(c, clean, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return;
                list.RemoveAt(idx);
                s.WeatherCities = list;
                if (list.Count == 0)
                {
                    s.WeatherCity = string.Empty;
                    s.WeatherCityIndex = 0;
                }
                else
                {
                    s.WeatherCityIndex = Math.Clamp(s.WeatherCityIndex, 0, list.Count - 1);
                    s.WeatherCity = list[s.WeatherCityIndex];
                    nextActive = s.WeatherCity;
                }
                s.WeatherLatitude = null;
                s.WeatherLongitude = null;
                s.WeatherResolvedCity = string.Empty;
            });
            _lastQueryCity = string.Empty;
            Current = !string.IsNullOrEmpty(nextActive) && _cityCache.TryGetValue(nextActive, out var cached) ? cached : null;
            CitiesChanged?.Invoke();
            if (SettingsService.Current.OptInWeather)
                _ = RefreshIfNeededAsync(force: true);
        }

        public async Task<List<WeatherCitySearchResult>> SearchCitiesAsync(string query, CancellationToken ct = default)
        {
            string q = (query ?? string.Empty).Trim();
            if (q.Length < 2 || !SettingsService.Current.OptInWeather)
                return new List<WeatherCitySearchResult>();

            try
            {
                string lang = LocalizationService.NormalizeCode(SettingsService.Current.Language);
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(q)}&count=6&language={lang}&format=json";
                using var resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return new List<WeatherCitySearchResult>();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("results", out var results))
                    return new List<WeatherCitySearchResult>();

                var list = new List<WeatherCitySearchResult>();
                foreach (var item in results.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new WeatherCitySearchResult
                    {
                        Name = name,
                        Country = item.TryGetProperty("country", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                        Admin1 = item.TryGetProperty("admin1", out var a) ? a.GetString() ?? string.Empty : string.Empty,
                        Latitude = item.GetProperty("latitude").GetDouble(),
                        Longitude = item.GetProperty("longitude").GetDouble()
                    });
                }
                return list;
            }
            catch (OperationCanceledException)
            {
                return new List<WeatherCitySearchResult>();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return new List<WeatherCitySearchResult>();
            }
        }

        private WeatherService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsNotch/1.0");
        }

        public void InvalidateAndRefresh()
        {
            if (!SettingsService.Current.OptInWeather)
            {
                Current = null;
                LastError = null;
                WeatherUpdated?.Invoke();
                return;
            }

            _ = RefreshIfNeededAsync(force: true);
        }

        public async Task RefreshIfNeededAsync(bool force = false)
        {
            var s = SettingsService.Current;
            if (!s.OptInWeather)
            {
                Current = null;
                LastError = null;
                WeatherUpdated?.Invoke();
                return;
            }

            string city = (s.WeatherCity ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(city) && s.WeatherCities is { Count: > 0 })
                city = s.WeatherCities[((s.WeatherCityIndex % s.WeatherCities.Count) + s.WeatherCities.Count) % s.WeatherCities.Count];
            if (string.IsNullOrEmpty(city))
            {
                Current = null;
                LastError = "Stadt in Einstellungen festlegen";
                WeatherUpdated?.Invoke();
                return;
            }

            bool cityChanged = !string.Equals(_lastQueryCity, city, StringComparison.OrdinalIgnoreCase);
            if (!force && !cityChanged && Current != null &&
                DateTime.UtcNow - Current.FetchedAtUtc < TimeSpan.FromMinutes(20))
            {
                return;
            }

            if (!await _gate.WaitAsync(0))
                return;

            try
            {
                IsLoading = true;
                LastError = null;
                WeatherUpdated?.Invoke();

                double? lat = s.WeatherLatitude;
                double? lon = s.WeatherLongitude;
                string resolvedCity = s.WeatherResolvedCity;

                if (cityChanged || lat == null || lon == null || string.IsNullOrWhiteSpace(resolvedCity))
                {
                    var geo = await GeocodeCityAsync(city);
                    if (geo == null)
                    {
                        Current = null;
                        LastError = $"Ort „{city}“ nicht gefunden";
                        return;
                    }

                    lat = geo.Value.Lat;
                    lon = geo.Value.Lon;
                    resolvedCity = geo.Value.Name;
                    _lastQueryCity = city;

                    SettingsService.UpdateQuiet(cfg =>
                    {
                        cfg.WeatherLatitude = lat;
                        cfg.WeatherLongitude = lon;
                        cfg.WeatherResolvedCity = resolvedCity;
                    });
                }

                var snapshot = await FetchForecastAsync(lat.Value, lon.Value, resolvedCity);
                if (snapshot != null)
                {
                    _cityCache[city] = snapshot;
                    if (!string.IsNullOrWhiteSpace(resolvedCity))
                        _cityCache[resolvedCity] = snapshot;

                    string? currentSelected = GetActiveCity();
                    if (string.IsNullOrEmpty(currentSelected) ||
                        string.Equals(currentSelected, city, StringComparison.OrdinalIgnoreCase))
                    {
                        Current = snapshot;
                        LastError = null;
                    }
                }
                else
                {
                    LastError = "Wetterdaten nicht verfügbar";
                }
            }
            catch (Exception ex)
            {
                LastError = "Verbindungsfehler";
                App.LogError(ex);
            }
            finally
            {
                IsLoading = false;
                _gate.Release();
                WeatherUpdated?.Invoke();

                string? nowSelected = GetActiveCity();
                if (SettingsService.Current.OptInWeather &&
                    !string.IsNullOrEmpty(nowSelected) &&
                    !string.Equals(nowSelected, city, StringComparison.OrdinalIgnoreCase) &&
                    (!_cityCache.TryGetValue(nowSelected, out var cachedNow) ||
                     DateTime.UtcNow - cachedNow.FetchedAtUtc > TimeSpan.FromMinutes(15)))
                {
                    _ = RefreshIfNeededAsync(force: true);
                }
            }
        }

        private static async Task<(double Lat, double Lon, string Name)?> GeocodeCityAsync(string city)
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=de&format=json";
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            var first = results[0];
            double lat = first.GetProperty("latitude").GetDouble();
            double lon = first.GetProperty("longitude").GetDouble();
            string name = first.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;
            return (lat, lon, name);
        }

        private static async Task<WeatherSnapshot?> FetchForecastAsync(double lat, double lon, string cityName)
        {
            string latStr = lat.ToString("0.####", CultureInfo.InvariantCulture);
            string lonStr = lon.ToString("0.####", CultureInfo.InvariantCulture);
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}&current=temperature_2m,weather_code,is_day,precipitation,rain,showers,cloud_cover&daily=temperature_2m_max,temperature_2m_min,weather_code&timezone=auto&forecast_days=3";

            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current", out var current))
                return null;

            double precip = current.TryGetProperty("precipitation", out var pProp) ? pProp.GetDouble() : 0;
            double rain = current.TryGetProperty("rain", out var rProp) ? rProp.GetDouble() : 0;
            double showers = current.TryGetProperty("showers", out var sProp) ? sProp.GetDouble() : 0;
            int cloudCover = current.TryGetProperty("cloud_cover", out var cProp) ? cProp.GetInt32() : 0;

            var snap = new WeatherSnapshot
            {
                CityName = cityName,
                TemperatureC = current.GetProperty("temperature_2m").GetDouble(),
                WeatherCode = current.GetProperty("weather_code").GetInt32(),
                IsDay = !current.TryGetProperty("is_day", out var dayProp) || dayProp.GetInt32() != 0,
                PrecipitationMm = Math.Max(precip, rain + showers),
                CloudCoverPercent = Math.Clamp(cloudCover, 0, 100),
                FetchedAtUtc = DateTime.UtcNow
            };

            if (root.TryGetProperty("daily", out var daily) &&
                daily.TryGetProperty("time", out var times) &&
                daily.TryGetProperty("temperature_2m_max", out var maxArr) &&
                daily.TryGetProperty("temperature_2m_min", out var minArr) &&
                daily.TryGetProperty("weather_code", out var codeArr))
            {
                int count = times.GetArrayLength();
                for (int i = 0; i < count; i++)
                {
                    if (DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        double min = minArr[i].GetDouble();
                        double max = maxArr[i].GetDouble();
                        int code = codeArr[i].GetInt32();
                        snap.ForecastDays.Add(new WeatherForecastDay
                        {
                            Date = dt,
                            MinC = min,
                            MaxC = max,
                            WeatherCode = code
                        });
                        if (i == 0)
                        {
                            snap.TodayMinC = min;
                            snap.TodayMaxC = max;
                        }
                    }
                }
            }

            return snap;
        }

        public static string DescribeCondition(int code, double precipitationMm)
        {
            if (code is (>= 51 and <= 67) or (>= 80 and <= 82))
            {
                if (precipitationMm >= 5.0 || code is 65 or 67 or 82)
                    return "Starker Regen";
                if (precipitationMm >= 1.5 || code is 55 or 57 or 63 or 81)
                    return "Regen";
                return code is (>= 51 and <= 57) && precipitationMm < 0.4 ? "Nieselregen" : "Leichter Regen";
            }
            return DescribeWmoCode(code);
        }

        public static string DescribeWmoCode(int code) => code switch
        {
            0 => "Klar",
            1 => "Überwiegend klar",
            2 => "Leicht bewölkt",
            3 => "Bewölkt",
            45 or 48 => "Nebel",
            51 or 53 or 55 or 56 or 57 => "Nieselregen",
            61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "Regen",
            71 or 73 or 75 or 77 or 85 or 86 => "Schnee",
            95 or 96 or 99 => "Gewitter",
            _ => "Wechselhaft"
        };

        public static NotchIconKind MapWmoToIcon(int code, bool isDay = true) => code switch
        {
            0 or 1 => NotchIconKind.Sun,
            2 => isDay ? NotchIconKind.CloudSun : NotchIconKind.Cloud,
            3 => NotchIconKind.Cloud,
            45 or 48 => NotchIconKind.CloudFog,
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => NotchIconKind.CloudRain,
            71 or 73 or 75 or 77 or 85 or 86 => NotchIconKind.CloudSnow,
            95 or 96 or 99 => NotchIconKind.CloudLightning,
            _ => NotchIconKind.CloudSun
        };
    }
}
