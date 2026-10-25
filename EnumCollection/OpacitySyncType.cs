namespace MultiTrackHelper
{
    /// <summary>
    /// “同步透明度”把真轨道的透明度写到假轨道的哪个透明度上。
    /// 成员名保持不改：关卡里已按名字存了该选项的值，改名会让旧存档的值读不出来。
    /// </summary>
    public enum OpacitySyncType
    {
        /// <summary>面板显示为"轨道主色调的透明度"：改假轨道 trackColor 的 A 通道（#RRGGBBAA，100% → FF、0% → 00）。</summary>
        TrackOpacity = 0,
        /// <summary>面板显示为"对象装饰本身的透明度"：写 AddObject 的 trackOpacity 字段（对象面板里的"透明度"，单位 %）。</summary>
        Decoration = 1
    }
}
