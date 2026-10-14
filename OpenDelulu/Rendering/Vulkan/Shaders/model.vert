#version 450
// Same lighting model as the OpenGL ES shader in GlModelView; keep them in sync.
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;

layout(push_constant) uniform Push
{
    mat4 mvp;
    vec4 diffuse;
    vec4 lightAndPass; // xyz = light direction, w = 0 opaque pass / 1 translucent pass
} pc;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUV;

void main()
{
    vNormal = aNormal;
    vUV = aUV;
    gl_Position = pc.mvp * vec4(aPos, 1.0);
}
