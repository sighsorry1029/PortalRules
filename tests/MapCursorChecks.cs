#nullable enable annotations
using System;

namespace PortalRulesMapCursorChecks
{
    public enum CursorLockMode { None, Locked }
    public struct Vector3 { }
    public class Player { public static Player? m_localPlayer = new Player(); }
    public class Root { public bool activeInHierarchy = true; }
    public static class Game { public static bool m_noMap; }
    public class Minimap
    {
        public enum MapMode { Small, Large }
        public static Minimap? instance = new Minimap();
        public MapMode m_mode = MapMode.Large;
        public Root? m_largeRoot = new Root();
        public bool ThrowOnOpen;
        public void ShowPointOnMap(Vector3 point)
        {
            if (ThrowOnOpen) throw new InvalidOperationException("Injected map-open failure");
            m_mode = Game.m_noMap ? MapMode.Small : MapMode.Large;
        }
    }
    public static class ZInput
    {
        public static bool MouseActive = true;
        public static bool IsMouseActive() => MouseActive;
    }
    public static class ZCursor
    {
        public static CursorLockMode LockState;
        public static bool Requested;
        public static int Shows;
        public static void Show() { Requested = true; Shows++; }
        public static void SimulateCameraCapture() { LockState = CursorLockMode.Locked; Requested = false; }
    }
    public sealed class Controller
    {
        public bool IsSelecting;
        /* CURSOR_METHOD */
        /* OPEN_METHOD */
        public static void Open() => OpenMapAt(new Vector3());
    }
    public static class Checks
    {
        private static int count;
        private static void Check(bool condition, string label)
        { if (!condition) throw new Exception(label); count++; }
        private static void Unchanged(Controller controller, string label)
        {
            ZCursor.SimulateCameraCapture();
            int shows = ZCursor.Shows;
            controller.EnsureSelectionCursor();
            Check(!ZCursor.Requested && ZCursor.LockState == CursorLockMode.Locked && ZCursor.Shows == shows, label);
        }
        public static string Run()
        {
            var controller = new Controller();
            Unchanged(controller, "Ordinary map not affected");
            controller.IsSelecting = true;
            for (int frame = 0; frame < 3; frame++)
            {
                ZCursor.SimulateCameraCapture();
                controller.EnsureSelectionCursor();
                Check(ZCursor.Requested && ZCursor.LockState == CursorLockMode.None, "Capture overwritten in each selection frame");
            }
            controller.IsSelecting = false;
            Unchanged(controller, "Selection ended: vanilla recapture preserved");
            controller.IsSelecting = true;
            Minimap.instance!.m_mode = Minimap.MapMode.Small;
            Unchanged(controller, "Map closed before selection cleanup");
            Minimap.instance.m_mode = Minimap.MapMode.Large;
            Minimap.instance.m_largeRoot!.activeInHierarchy = false;
            Unchanged(controller, "Hidden map does not claim cursor");
            Minimap.instance.m_largeRoot = null;
            Unchanged(controller, "Destroyed map root");
            Minimap.instance.m_largeRoot = new Root();
            Player.m_localPlayer = null;
            Unchanged(controller, "Dedicated process or disconnected local player");
            Player.m_localPlayer = new Player();
            ZInput.MouseActive = false;
            Unchanged(controller, "Exclusive gamepad input preserved");
            ZInput.MouseActive = true;
            controller.EnsureSelectionCursor();
            Check(ZCursor.Requested, "Mouse return during selection restores cursor");
            Minimap.instance = null;
            Unchanged(controller, "Map destroyed");
            Minimap.instance = new Minimap();
            foreach (bool noMap in new[] { false, true })
            {
                Game.m_noMap = noMap;
                Controller.Open();
                Check(Game.m_noMap == noMap && Minimap.instance.m_mode == Minimap.MapMode.Large, "Open preserves world map setting");
                Minimap.instance.ThrowOnOpen = true;
                try { Controller.Open(); throw new Exception("Expected injected failure"); }
                catch (InvalidOperationException) { }
                Check(Game.m_noMap == noMap, "Failure restores world map setting");
                Minimap.instance.ThrowOnOpen = false;
            }
            return $"{count} map cursor/lifecycle checks passed (production methods; UI/input boundaries are test doubles).";
        }
    }
}
