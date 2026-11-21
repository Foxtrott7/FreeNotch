using WindowsNotch.UI;
using Xunit;

namespace WindowsNotch.Tests
{
    public sealed class SettingsViewTests
    {
        [Fact]
        public void CalculateWheelScrollStep_ScalesSmallTouchpadDeltasProportionally()
        {
            // Touchpad sends small deltas (e.g. -10 instead of -120): should scroll ~3 px instead of jumping 48 px.
            double touchpadStep = SettingsView.CalculateWheelScrollStep(delta: -10, viewportHeight: 400, wheelScrollLines: 3);
            double mouseNotchStep = SettingsView.CalculateWheelScrollStep(delta: -120, viewportHeight: 400, wheelScrollLines: 3);

            Assert.Equal(3.0, touchpadStep, precision: 3);
            Assert.Equal(36.0, mouseNotchStep, precision: 3);
        }

        [Fact]
        public void CalculateWheelScrollStep_ClampsBurstDeltasAndHandlesZero()
        {
            Assert.Equal(0.0, SettingsView.CalculateWheelScrollStep(delta: 0, viewportHeight: 400, wheelScrollLines: 3));
            Assert.Equal(96.0, SettingsView.CalculateWheelScrollStep(delta: -600, viewportHeight: 400, wheelScrollLines: 3));
            Assert.Equal(-96.0, SettingsView.CalculateWheelScrollStep(delta: 600, viewportHeight: 400, wheelScrollLines: 3));
        }

        [Fact]
        public void CalculateWheelScrollStep_SupportsPageScrollSetting()
        {
            double pageStep = SettingsView.CalculateWheelScrollStep(delta: -40, viewportHeight: 360, wheelScrollLines: -1);
            Assert.Equal(78.0, pageStep, precision: 3);
        }
    }
}
