using ADOFAI;
using System;

namespace MultiTrackHelper.Features
{
    /// <summary>
    /// 随机数源：开启"使用随机种子"时用种子化 System.Random（结果可复现），
    /// 否则保持与原版一致的 UnityEngine.Random 行为。
    /// </summary>
    internal sealed class RandomSource
    {
        private readonly System.Random rng;

        private RandomSource(int seed)
        {
            rng = new System.Random(seed);
        }

        private RandomSource()
        {
        }

        public static RandomSource From(LevelEvent dataPanel)
        {
            return dataPanel.Get<bool>("useSeed")
                ? new RandomSource(dataPanel.Get<int>("seed"))
                : new RandomSource();
        }

        public float Range(float min, float max)
        {
            if (rng == null)
                return UnityEngine.Random.Range(min, max);
            if (max <= min)
                return min;
            return (float)rng.NextDouble() * (max - min) + min;
        }
    }
}
