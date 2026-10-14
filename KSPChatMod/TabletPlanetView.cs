using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        PlanetViewCamera planetView;
        double planetViewYaw;

        void TabletPlanetView(float width, CelestialBody body)
        {
            TabletUi.Ensure(tabletFont);
            if (body == null) return;
            float height = Mathf.Clamp(width * .65f, 160, 320);
            Rect panel = GUILayoutUtility.GetRect(width, height + 110);
            Rect content = TabletUi.CardPanel(panel, "PLANET LIVE VIEW");
            Rect picture = new Rect(content.x, content.y, content.width, height - 40);
            if (planetView == null) planetView = gameObject.AddComponent<PlanetViewCamera>();
            if (Event.current.type == EventType.Repaint)
                planetView.Refresh(body, planetViewYaw, picture.width / picture.height);
            if (planetView.Image != null && planetView.Body == body)
                GUI.DrawTexture(picture, planetView.Image, ScaleMode.ScaleToFit, false);
            else TabletUi.Label(picture, "Scaled-space view unavailable.", true);
            float y = picture.yMax + 6, w = (content.width - 16) / 3;
            if (TabletUi.Button(new Rect(content.x, y, w, 44), "LEFT", false, TabletLink) && TabletLink)
                planetViewYaw = PlanetViewMath.Wrap(planetViewYaw - 20);
            if (TabletUi.Button(new Rect(content.x + w + 8, y, w, 44), "RESET", false, TabletLink) && TabletLink)
                planetViewYaw = 0;
            if (TabletUi.Button(new Rect(content.x + 2 * (w + 8), y, w, 44), "RIGHT", false, TabletLink) && TabletLink)
                planetViewYaw = PlanetViewMath.Wrap(planetViewYaw + 20);
            GUILayout.Label(PlanetViewBlurb(body), tabletText);
            GUILayout.Label(TabletLink ? "Live scaled-space model; not a terrain survey." :
                "INFO ONLY - LIVE LINK off. Live scaled-space model; not a terrain survey.", tabletText);
        }

        // Original descriptions based on live properties, also valid for added planetary systems.
        static string PlanetViewBlurb(CelestialBody body)
        {
            if (IsStar(body)) return "The system's central light source. Plan departures around the orbits of its worlds.";
            string place = IsStar(body.referenceBody) ? "A world orbiting " : "A satellite orbiting ";
            string air = body.atmosphere ? " Its atmosphere makes arrival speed and heating part of the landing plan." :
                " With no atmosphere to brake against, a landing needs propulsion all the way down.";
            return place + body.referenceBody.bodyName + "." + air;
        }
    }

    // Own camera only: never move the flight/map camera or alter body layers/materials.
    // A short inactivity lease releases GPU resources on page/window close; destruction
    // and scene changes release them immediately through the owner's GameObject.
    internal sealed class PlanetViewCamera : MonoBehaviour
    {
        internal RenderTexture Image { get; private set; }
        internal CelestialBody Body { get; private set; }
        Camera view;
        float touched, nextFrame;

        internal void Refresh(CelestialBody body, double yaw, float aspect)
        {
            touched = Time.realtimeSinceStartup;
            if (body == null || body.scaledBody == null || !body.scaledBody.activeInHierarchy)
            { Release(); return; }
            if (Body == body && touched < nextFrame) return;
            nextFrame = touched + .1f;
            if (view == null)
            {
                var host = new GameObject("AICS planet preview camera");
                host.transform.SetParent(transform, false);
                view = host.AddComponent<Camera>();
                view.enabled = false;
                view.clearFlags = CameraClearFlags.SolidColor;
                view.backgroundColor = Color.black;
                Image = new RenderTexture(512, 512, 24);
                Image.Create();
                view.targetTexture = Image;
            }
            Vector3 center = body.scaledBody.transform.position;
            // Derive the radius through KSP's conversion rather than assuming its scale factor.
            double radius = ((Vector3d)ScaledSpace.LocalToScaledSpace(body.position + Vector3d.right * body.Radius) -
                (Vector3d)ScaledSpace.LocalToScaledSpace(body.position)).magnitude;
            double distance = PlanetViewMath.Distance(radius, 35, aspect);
            if (double.IsNaN(distance) || double.IsInfinity(distance) || distance > float.MaxValue / 4)
            { Release(); return; }
            view.fieldOfView = 35;
            // Square render target displays square pixels; fit for the narrower UI dimension.
            view.aspect = 1;
            distance = Math.Max(distance, PlanetViewMath.Distance(radius, 35, 1));
            Vector3 direction = Quaternion.Euler(15, (float)yaw, 0) * Vector3.forward;
            view.transform.position = center + direction * (float)distance;
            view.transform.LookAt(center);
            view.nearClipPlane = (float)Math.Max(.001, distance - radius * 1.5);
            view.farClipPlane = (float)(distance + radius * 3);
            view.cullingMask = 1 << body.scaledBody.layer;
            RenderTexture previous = RenderTexture.active;
            try { view.Render(); }
            finally { RenderTexture.active = previous; }
            Body = body;
        }

        void Update() { if (view != null && Time.realtimeSinceStartup - touched > .5f) Release(); }
        void OnDisable() { Release(); }
        void OnDestroy() { Release(); }
        void Release()
        {
            if (view != null) { view.targetTexture = null; Destroy(view.gameObject); view = null; }
            if (Image != null) { Image.Release(); Destroy(Image); Image = null; }
            Body = null;
        }
    }
}
