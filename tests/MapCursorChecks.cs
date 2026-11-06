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
        public float m_leftDownTime;
        public bool m_dragView;
        public int Clicks;
        public int PanSteps;
        public bool ThrowOnClick;

        // Controlled UI/game boundary: down arms panning, and a normal up
        // invokes the click before clearing state (Valheim 1.0.21 ordering).
        public void PointerDown() { m_leftDownTime = 10f; }
        public void PointerUp()
        {
            if (m_leftDownTime != 0f)
            {
                Clicks++;
                if (ThrowOnClick) throw new InvalidOperationException("Injected click failure");
                m_leftDownTime = 0f;
            }
            m_dragView = false;
        }
        public void MovePointer()
        {
            if (m_leftDownTime != 0f) m_dragView = true;
            if (m_dragView) PanSteps++;
        }
        public void ShowPointOnMap(Vector3 point)
        {
            if (ThrowOnOpen) throw new InvalidOperationException("Injected map-open failure");
            m_mode = Game.m_noMap ? MapMode.Small : MapMode.Large;
        }
    }
    public static class ZInput
    {
        public static bool MouseActive = true;
        public static bool TouchActive;
        public static bool GamepadActive;
        public static bool MouseHeld;
        public static bool IsMouseActive() => MouseActive;
        public static bool IsTouchActive() => TouchActive;
        public static bool IsGamepadActive() => GamepadActive;
        public static bool GetMouseButton(int button) => MouseHeld;
    }
    public static class PortalRulesPlugin
    {
        public static readonly Logger PortalRulesLogger = new Logger();
        public class Logger
        {
            public int Warnings;
            public void LogWarning(string message) { Warnings++; }
        }
    }
    public static class AccessTools
    {
        public delegate ref F FieldRef<T, F>(T instance);
        public static bool FailBinding;
        public static int Binds;
        private static ref float DownTime(Minimap map) => ref map.m_leftDownTime;
        private static ref bool DragView(Minimap map) => ref map.m_dragView;
        public static FieldRef<T, F> FieldRefAccess<T, F>(string field)
        {
            Binds++;
            if (FailBinding) throw new MissingFieldException(field);
            if (field == "m_leftDownTime") return (FieldRef<T, F>)(object)(FieldRef<Minimap, float>)DownTime;
            if (field == "m_dragView") return (FieldRef<T, F>)(object)(FieldRef<Minimap, bool>)DragView;
            throw new MissingFieldException(field);
        }
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
        private static Action<Minimap>? ResetMapDrag = CreateMapDragReset();
        /* CURSOR_METHOD */
        /* OPEN_METHOD */
        /* DRAG_METHODS */
        public static void Open() => OpenMapAt(new Vector3());
        public static void ResetDrag(Minimap map) => ResetMapDrag?.Invoke(map);
        public static void RebindDrag() => ResetMapDrag = CreateMapDragReset();
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
            CheckDragRecovery(controller);
            return $"{count} map cursor/lifecycle checks passed (production methods; UI/input boundaries are test doubles).";
        }

        private static void CheckDragRecovery(Controller controller)
        {
            var map = new Minimap();
            Minimap.instance = map;
            controller.IsSelecting = true;
            Controller.ResetDrag(map);
            int binds = AccessTools.Binds;

            // UI Update precedes LateUpdate. A normal portal click must run
            // exactly once even though the held button is already released.
            map.PointerDown();
            ZInput.MouseHeld = false;
            controller.EnsureSelectionCursor();
            Check(map.m_leftDownTime != 0f, "Camera cursor repair must not consume a pending click");
            map.PointerUp();
            controller.RecoverReleasedMapDrag();
            Check(map.Clicks == 1 && !map.m_dragView && map.m_leftDownTime == 0f,
                "Normal UI click survives before late recovery");

            map.PointerDown();
            ZInput.MouseHeld = true;
            map.MovePointer();
            controller.RecoverReleasedMapDrag();
            map.MovePointer();
            Check(map.m_dragView && map.m_leftDownTime != 0f && map.PanSteps == 2,
                "Held mouse drag is uninterrupted");
            ZInput.MouseHeld = false;
            map.PointerUp();
            controller.RecoverReleasedMapDrag();
            int panSteps = map.PanSteps;
            map.MovePointer();
            Check(map.PanSteps == panSteps && !map.m_dragView,
                "Released drag no longer follows the pointer");

            // No prior held sample: the whole down/up sequence may have
            // happened before the first LateUpdate that observes this map.
            map.PointerDown();
            map.MovePointer();
            int clicks = map.Clicks;
            controller.RecoverReleasedMapDrag();
            panSteps = map.PanSteps;
            map.MovePointer();
            Check(!map.m_dragView && map.m_leftDownTime == 0f && map.PanSteps == panSteps,
                "Lost pointer-up is recovered without observing an earlier held frame");
            Check(map.Clicks == clicks, "Recovery does not synthesize a teleport click");

            map.PointerDown();
            map.MovePointer();
            map.ThrowOnClick = true;
            try { map.PointerUp(); throw new Exception("Expected click failure"); }
            catch (InvalidOperationException) { }
            clicks = map.Clicks;
            controller.RecoverReleasedMapDrag();
            Check(!map.m_dragView && map.m_leftDownTime == 0f && map.Clicks == clicks,
                "Failed click cleanup is recovered without retrying the action");
            map.ThrowOnClick = false;

            map.PointerDown();
            map.m_dragView = false;
            controller.RecoverReleasedMapDrag();
            map.MovePointer();
            Check(!map.m_dragView && map.m_leftDownTime == 0f,
                "Stale down timestamp cannot rearm a cleared drag");

            Action<string> unchanged = label =>
            {
                map.PointerDown();
                map.m_dragView = true;
                controller.RecoverReleasedMapDrag();
                Check(map.m_leftDownTime != 0f && map.m_dragView, label);
            };
            controller.IsSelecting = false;
            unchanged("Ordinary map input is untouched");
            controller.IsSelecting = true;
            ZInput.TouchActive = true;
            unchanged("Touch input is untouched even with a mouse connected");
            ZInput.TouchActive = false;
            ZInput.GamepadActive = true;
            unchanged("Virtual gamepad mouse drag is untouched");
            ZInput.GamepadActive = false;
            ZInput.MouseActive = false;
            unchanged("Non-mouse input is untouched");
            ZInput.MouseActive = true;
            map.m_mode = Minimap.MapMode.Small;
            unchanged("Closed map is untouched before selection cleanup");
            map.m_mode = Minimap.MapMode.Large;
            map.m_largeRoot!.activeInHierarchy = false;
            unchanged("Hidden map is untouched");
            map.m_largeRoot = null;
            unchanged("Destroyed map root is safe");
            map.m_largeRoot = new Root();
            Player.m_localPlayer = null;
            unchanged("Dedicated server or disconnected local player is safe");
            Player.m_localPlayer = new Player();
            Minimap.instance = null;
            unchanged("Destroyed map is safe");
            Minimap.instance = map;
            controller.RecoverReleasedMapDrag();
            Check(map.m_leftDownTime == 0f && !map.m_dragView, "Mouse return recovers stale input");

            ZInput.MouseHeld = true;
            map.PointerDown();
            map.MovePointer();
            Controller.ResetDrag(map);
            Check(map.m_leftDownTime == 0f && !map.m_dragView,
                "Selection boundary reset clears both fields even while held");
            ZInput.MouseHeld = false;
            Check(AccessTools.Binds == binds, "Per-frame recovery does not repeat reflection binding");

            AccessTools.FailBinding = true;
            Controller.RebindDrag();
            unchanged("Unavailable private fields disable only recovery");
            Check(PortalRulesPlugin.PortalRulesLogger.Warnings == 1, "Binding failure emits one warning");
            AccessTools.FailBinding = false;
            Controller.RebindDrag();
        }
    }
}
