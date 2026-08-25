using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AdaptiveEnemies.Runtime
{
    /// <summary>
    /// Debug noclip. Press <b>1</b> to toggle free movement, WASD or the arrow keys to fly,
    /// Shift for triple speed.
    ///
    /// <para>Purely a testing aid: an enemy that learns to overshoot can strand itself, or
    /// strand you, somewhere the level did not anticipate. This exists so a stuck run can be
    /// rescued instead of restarted. It changes nothing about learning and is inert until
    /// the key is pressed.</para>
    /// </summary>
    internal class DebugFreeMove : MonoBehaviour
    {
        private bool _active;

        private Rigidbody2D _rb;
        private bool _savedKinematic;
        private float _savedGravity;
        private RigidbodyConstraints2D _savedConstraints;

        private void Update()
        {
            if (!ModConfig.DebugFreeMove.Value)
            {
                if (_active) Disengage();
                return;
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.digit1Key.wasPressedThisFrame)
            {
                if (_active) Disengage(); else Engage();
            }

            if (_active) Fly(keyboard);
        }

        private static Transform Hero()
        {
            var hc = HeroController.UnsafeInstance;
            return hc != null ? hc.transform : null;
        }

        private void Engage()
        {
            var hero = Hero();
            if (hero == null) { Log.Warn("Free move: no hero to move."); return; }

            _rb = hero.GetComponent<Rigidbody2D>();
            if (_rb != null)
            {
                _savedKinematic = _rb.bodyType == RigidbodyType2D.Kinematic;
                _savedGravity = _rb.gravityScale;
                _savedConstraints = _rb.constraints;

                _rb.linearVelocity = Vector2.zero;
                _rb.gravityScale = 0f;
                _rb.bodyType = RigidbodyType2D.Kinematic;
            }

            _active = true;
            Log.Info("Free move ON — WASD/arrows to fly, Shift for triple speed, 1 to exit.");
        }

        private void Disengage()
        {
            if (_rb != null)
            {
                _rb.bodyType = _savedKinematic ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
                _rb.gravityScale = _savedGravity;
                _rb.constraints = _savedConstraints;
                _rb.linearVelocity = Vector2.zero;
            }

            _rb = null;
            _active = false;
            Log.Info("Free move OFF.");
        }

        private void Fly(Keyboard keyboard)
        {
            var hero = Hero();
            if (hero == null) { Disengage(); return; }

            float x = 0f, y = 0f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) x -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) x += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) y -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) y += 1f;

            if (x == 0f && y == 0f) return;

            float speed = ModConfig.DebugFreeMoveSpeed.Value;
            if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) speed *= 3f;

            var delta = new Vector3(x, y, 0f).normalized * speed * Time.unscaledDeltaTime;

            // Move the body rather than the transform where possible, so the physics system
            // stays consistent with where the hero actually is.
            if (_rb != null) _rb.position += (Vector2)delta;
            else hero.position += delta;
        }

        private void OnDisable()
        {
            if (_active) Disengage();
        }
    }
}
