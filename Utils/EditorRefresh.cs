namespace MultiTrackHelper.Utils
{
    /// <summary>One refresh boundary after a user-facing event write. Never rebuild track geometry.</summary>
    internal static class EditorRefresh
    {
        internal static void AfterWrite(scnEditor editor)
        {
            if (editor == null) return;
            // RemakePath(true, false) does NOT call this inside the game.
            editor.ApplyEventsToFloors();
            // ApplyEventsToFloors schedules decoration preview refresh for the next editor frame.
            // Event indicators are separate from floor icons and do not refresh when selection is unchanged.
            if (editor.SelectionIsSingle())
            {
                scrFloor floor = editor.selectedFloors[0];
                editor.levelEventsPanel?.ShowTabsForFloor(floor.seqID);
                editor.ShowEventIndicators(floor);
            }
        }
    }
}
