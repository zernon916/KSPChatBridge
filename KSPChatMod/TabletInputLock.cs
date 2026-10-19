// GROK TAB3: mouse wheel over the tablet scrolls the tablet, not the flight camera. Camera controls are locked only while the
// mouse is over the visible tablet window; released when it leaves, the tablet closes, or the scene changes (addon destroyed).
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TabletInputLock : MonoBehaviour
    {
        const string LockId = "AICS_TabletLock";
        bool locked;
        void Update()
        {
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            Set(AicsMenu.TabletVisible && AicsMenu.TabletScreenRect.Contains(m));
        }
        void Set(bool on)
        {
            if (on == locked) return;
            locked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }
        void OnDisable() { Set(false); }
        void OnDestroy() { locked = true; Set(false); }
    }
}
