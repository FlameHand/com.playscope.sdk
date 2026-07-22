using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Merge2048.Core;

namespace Merge2048.Presentation
{
    public sealed class InputReader : MonoBehaviour
    {
        private const float MIN_SWIPE_DISTANCE_PX = 60f;

        private bool _isPointerTracking;
        private Vector2 _pointerDownPosition;

        public event Action<Direction> DirectionPerformed;

        private void Update()
        {
            ReadKeyboard();
            ReadPointerSwipe();
        }

        private void ReadKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.upArrowKey.wasPressedThisFrame)
            {
                DirectionPerformed?.Invoke(Direction.Up);
            }
            else if (keyboard.downArrowKey.wasPressedThisFrame)
            {
                DirectionPerformed?.Invoke(Direction.Down);
            }
            else if (keyboard.leftArrowKey.wasPressedThisFrame)
            {
                DirectionPerformed?.Invoke(Direction.Left);
            }
            else if (keyboard.rightArrowKey.wasPressedThisFrame)
            {
                DirectionPerformed?.Invoke(Direction.Right);
            }
        }

        private void ReadPointerSwipe()
        {
            var pointer = Pointer.current;
            if (pointer == null)
            {
                return;
            }

            if (pointer.press.wasPressedThisFrame)
            {
                _pointerDownPosition = pointer.position.ReadValue();
                _isPointerTracking = true;
            }

            if (pointer.press.wasReleasedThisFrame)
            {
                if (_isPointerTracking)
                {
                    var releasedPosition = pointer.position.ReadValue();
                    EvaluateSwipe(_pointerDownPosition, releasedPosition);
                }

                _isPointerTracking = false;
            }
        }

        private void EvaluateSwipe(Vector2 startPosition, Vector2 endPosition)
        {
            var delta = endPosition - startPosition;

            if (delta.magnitude < MIN_SWIPE_DISTANCE_PX)
            {
                return;
            }

            if (Mathf.Abs(delta.x) > Mathf.Abs(delta.y))
            {
                DirectionPerformed?.Invoke(delta.x > 0f ? Direction.Right : Direction.Left);
            }
            else
            {
                DirectionPerformed?.Invoke(delta.y > 0f ? Direction.Up : Direction.Down);
            }
        }
    }
}
