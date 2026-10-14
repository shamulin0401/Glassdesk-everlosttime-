namespace GlassDesk.Dock;
public static class PassiveRevealPolicy
{
    /// <summary>
    /// 边缘唤出的门控：热区命中、边缘唤出开启、且没有被"全屏抑制"或"遮挡屏蔽"挡掉。
    /// </summary>
    /// <remarks>
    /// 生产路径当前只走 <see cref="CanRevealDuringSuppression"/>（边缘唤出那一族设置已作废），
    /// 但这个方法不是死代码：<c>Phase6DisplayTests.PassiveRevealHonorsIndependentGates</c>
    /// 用反射逐条核对它——它是这组门的**规格表**。要删它得先决定"这些门还要不要"，
    /// 而不是看有没有调用者。
    /// </remarks>
    public static bool CanReveal(bool inHotzone,bool edgeEnabled,bool fullscreen,bool suppressFullscreen,bool covered,bool blockCovered)
        => inHotzone && edgeEnabled && !(fullscreen && suppressFullscreen) && !(covered && blockCovered);

    /// <summary>遮挡或全屏期间，关闭唤出锁后允许鼠标移到 Dock 原位置唤出。</summary>
    public static bool CanRevealDuringSuppression(bool pointerOverDock, bool revealLocked)
        => !revealLocked && pointerOverDock;
}
