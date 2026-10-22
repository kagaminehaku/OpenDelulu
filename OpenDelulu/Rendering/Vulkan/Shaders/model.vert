#version 450
// Same lighting model as the OpenGL ES shader in GlModelView; keep them in sync.
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUV;
layout(location = 3) in float aGloss;
layout(location = 4) in vec4 aColor; // Direct3D 9 fixed-function lighting computed on the CPU; a = 0: not lit

layout(push_constant) uniform Push
{
    mat4 mvp;
    vec4 diffuse;
    vec4 lightAndPass; // xyz = light direction, w = 0 opaque pass / 1 translucent pass
    vec4 ambient;
    vec4 emissive;
} pc;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUV;
layout(location = 2) out float vGloss;
layout(location = 3) out vec4 vColor;

void main()
{
    vNormal = aNormal;
    vUV = aUV;
    vGloss = aGloss;
    vColor = aColor;
    gl_Position = pc.mvp * vec4(aPos, 1.0);
}
