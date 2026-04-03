// FlyCamera.cs — Simple editor fly camera for Play mode testing.
// WASD to move, right-click + drag to look, Q/E to go down/up, Shift to sprint.

using UnityEngine;
using UnityEngine.InputSystem;

public class FlyCamera : MonoBehaviour
{
    [SerializeField] private float moveSpeed        = 3f;
    [SerializeField] private float lookSpeed        = 0.15f;
    [SerializeField] private float sprintMultiplier = 3f;

    private float _yaw;
    private float _pitch;

    private void Start()
    {
        _yaw   = transform.eulerAngles.y;
        _pitch = transform.eulerAngles.x;
    }

    private void Update()
    {
        var mouse    = Mouse.current;
        var keyboard = Keyboard.current;
        if (mouse == null || keyboard == null) return;

        // Look — only while right mouse button is held
        if (mouse.rightButton.isPressed)
        {
            var delta = mouse.delta.ReadValue();
            _yaw   += delta.x * lookSpeed;
            _pitch -= delta.y * lookSpeed;
            _pitch  = Mathf.Clamp(_pitch, -89f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        float speed = moveSpeed * (keyboard.leftShiftKey.isPressed ? sprintMultiplier : 1f);

        Vector3 dir = Vector3.zero;
        if (keyboard.wKey.isPressed) dir += transform.forward;
        if (keyboard.sKey.isPressed) dir -= transform.forward;
        if (keyboard.aKey.isPressed) dir -= transform.right;
        if (keyboard.dKey.isPressed) dir += transform.right;
        if (keyboard.eKey.isPressed) dir += Vector3.up;
        if (keyboard.qKey.isPressed) dir -= Vector3.up;

        transform.position += dir * speed * Time.deltaTime;
    }
}
