#version 450
layout(set = 0, binding = 0) uniform sampler2D uTex;

layout(push_constant) uniform Push
{
    mat4 mvp;
    vec4 diffuse;
    vec4 lightAndPass;
} pc;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUV;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 texel = texture(uTex, vUV);
    float alpha = texel.a;
    if (pc.lightAndPass.w < 0.5) {
        if (alpha < 0.5) discard;
    } else {
        if (alpha >= 0.5 || alpha < 0.02) discard;
    }
    float lambert = abs(dot(normalize(vNormal), pc.lightAndPass.xyz));
    outColor = vec4(texel.rgb * pc.diffuse.rgb * (0.45 + 0.6 * lambert), alpha);
}
