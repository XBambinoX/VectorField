#version 450

layout(push_constant) uniform PushConstants {
    mat4 transform;
} pc;

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec4 inColor;

layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = pc.transform * vec4(inPosition, 0.0, 1.0);
    vColor = inColor;
}
