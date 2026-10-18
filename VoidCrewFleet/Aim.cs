using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Where the local player is looking, for punching and the hacking terminal. It's the game's own interaction ray,
    /// read while the game aims it. In VR (with the VR UI Fix mod) that ray follows the head, but the main camera keeps
    /// the mouse-look direction the rest of the frame, so aiming with the camera missed. Falls back to the main camera.
    /// </summary>
    internal static class Aim
    {
        private const float FreshSeconds = 0.3f;

        private static Vector3 _eye;
        private static Vector3 _forward;
        private static float _time = -1f;

        [GamePatch(PvpBoarding.Feature, "CG.Client.Player.Interactions.RaycastHandler", "FindInteractable", PatchKind.Postfix)]
        private static void FindInteractablePostfix(Transform ___cameraTransform)
        {
            if (___cameraTransform == null) return;
            _eye = ___cameraTransform.position;
            _forward = ___cameraTransform.forward;
            _time = Time.unscaledTime;
        }

        public static bool Get(out Vector3 eye, out Vector3 forward)
        {
            if (_time >= 0f && Time.unscaledTime - _time < FreshSeconds)
            {
                eye = _eye;
                forward = _forward;
                return true;
            }
            Camera camera = Camera.main;
            if (camera == null)
            {
                eye = Vector3.zero;
                forward = Vector3.forward;
                return false;
            }
            eye = camera.transform.position;
            forward = camera.transform.forward;
            return true;
        }
    }
}
