using System;

namespace OurTaiko
{
    // Increments follow OpenTaiko's normal gauge (CAct演奏ゲージ共通.Init), which looks only at the
    // stars; the clear tier keeps the per-course values (Easy 60, Normal/Hard 70, Oni/Edit 80).
    public readonly struct SoulGaugeRules
    {
        // Share of a route's notes that, all 良, fill the gauge (fGaugeMaxRate, in percent).
        public float GaugeRate { get; }
        // A 不可 costs this many 良 increments (dbDamageRate).
        public float DamageRate { get; }
        public int ClearPoints { get; }

        internal SoulGaugeRules(float gaugeRate, float damageRate, int clearPoints)
        {
            GaugeRate = gaugeRate; DamageRate = damageRate; ClearPoints = clearPoints;
        }

        public static SoulGaugeRules ForChart(string course, int level)
        {
            int difficulty;
            switch (course?.ToLowerInvariant())
            {
                case "easy": difficulty = 0; break;
                case "normal": difficulty = 1; break;
                case "hard": difficulty = 2; break;
                case "oni": case "edit": difficulty = 3; break;
                default: throw new NotSupportedException("Unsupported soul gauge course: " + course);
            }
            // Player::reset_chart treats absent/zero LEVEL as Oni 10 for the clear tier.
            int clear = level == 0 || difficulty == 3 ? 8000 : difficulty == 0 ? 6000 : 7000;
            // OpenTaiko reads an absent LEVEL as 0; ★0–7 and ★8 forgive misses, ★9 and above
            // (★11–13 included: their rates are listed but never selected) do not.
            switch (Math.Max(0, level))
            {
                case 0: case 1: case 2: case 3: case 4: case 5: case 6: case 7:
                    return new SoulGaugeRules(70.7f, 0.625f, clear);
                case 8:
                    return new SoulGaugeRules(70f, 0.625f, clear);
                default:
                    return new SoulGaugeRules(75.0f, 2.0f, clear);
            }
        }
    }
}
