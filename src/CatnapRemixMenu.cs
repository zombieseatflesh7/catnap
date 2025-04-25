using Menu.Remix.MixedUI;
using UnityEngine;

namespace Catnap
{
    internal class CatnapRemixMenu : OptionInterface
    {
        readonly Configurable<int> speedMultiplier;
        readonly Configurable<int> volumeMultiplier;

        internal CatnapRemixMenu(Plugin plugin)
        {
            speedMultiplier = config.Bind<int>("catnap_SpeedMultiplier", 6);
            volumeMultiplier = config.Bind<int>("catnap_VolumeMultiplier", 25);

            ConfigOnChange();
            OnConfigChanged += ConfigOnChange;
        }

        public override void Initialize()
        {
            OpTab configTab = new OpTab(this, "Config");
            Tabs = new OpTab[] { configTab };

            configTab.AddItems( //create an array of ui elements
                new OpLabel(new Vector2(300, 550), new Vector2(0, 30), "Sleep Settings", FLabelAlignment.Center, true),
                new OpLabel(365, 486, "Sleep Speed Multiplier"),
                new OpSlider(speedMultiplier, new Vector2(50, 480), 300) { min = 2, max = 10, hideLabel = false, description = "Default: 6 - Time will move at X times speed while sleeping." },
                new OpLabel(365, 406, "Sleep Volume Percent"),
                new OpSlider(volumeMultiplier, new Vector2(50, 400), 300) { max = 100, hideLabel = false, description = "Default: 25 - The volume will be reduced to X percent while sleeping." }
                );
        }

        void ConfigOnChange()
        {
            Plugin.maxSpeedMultiplier = speedMultiplier.Value;
            //Plugin.maxSleeping = Plugin.startSleeping + (speedMultiplier.Value - 1) * 80;

            Plugin.sleepVolumeMult = volumeMultiplier.Value / 100f;
        }
    }
}
