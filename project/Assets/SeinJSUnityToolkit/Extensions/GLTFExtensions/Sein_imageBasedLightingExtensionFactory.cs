﻿/**
 * @File   : Sein_imageBasedLightingExtensionFactory.cs
 * @Author : dtysky (dtysky@outlook.com)
 * @Link   : dtysky.moe
 * @Date   : 2019/10/12 0:00:00PM
 */
using System;
using Newtonsoft.Json.Linq;
using GLTF.Math;
using Newtonsoft.Json;
using GLTF.Extensions;
using System.Collections.Generic;
using GLTF.Schema;
using UnityEngine;
using System.IO;
using UnityEditor;

namespace SeinJS
{
    public class Sein_imageBasedLightingExtensionFactory : SeinExtensionFactory
    {
        public override string GetExtensionName() { return "Sein_imageBasedLighting"; }
        public override List<EExtensionType> GetExtensionTypes() { return new List<EExtensionType> { EExtensionType.Global, EExtensionType.Material }; }
        private static Texture2D brdfLUT;

        private Dictionary<ExporterEntry, Dictionary<Cubemap, int>> _cache = new Dictionary<ExporterEntry, Dictionary<Cubemap, int>>();
        private Dictionary<ExporterEntry, int> _onlyLightingId = new Dictionary<ExporterEntry, int>();

        public override void BeforeExport()
        {
            _cache.Clear();
            _onlyLightingId.Clear();
            var brdfPath = "Assets/SeinJSUnityToolkit/Shaders/brdfLUT.jpg";
            brdfLUT = AssetDatabase.LoadAssetAtPath<Texture2D>(brdfPath);
        }

        public override void FinishExport()
        {
            _cache.Clear();
            _onlyLightingId.Clear();
        }

        public override void Serialize(ExporterEntry entry, Dictionary<string, Extension> extensions, UnityEngine.Object component = null, object options = null)
        {
            var mat = component as UnityEngine.Material;
            var hasReflection = ExporterSettings.Lighting.reflection && mat.GetInt("envReflection") != (int)SeinPBRShaderGUI.EnvReflection.Off;
            var hasLighting = RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox || RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Trilight;

            if (!hasReflection && !hasLighting)
            {
                return;
            }

            if (entry.root.Extensions == null)
            {
                entry.root.Extensions = new Dictionary<string, Extension>();
            }

            Sein_imageBasedLightingExtension globalExtension;
            if (!entry.root.Extensions.ContainsKey(ExtensionName))
            {
                globalExtension = new Sein_imageBasedLightingExtension();
                globalExtension.isGlobal = true;
                AddExtension(entry.root.Extensions, globalExtension);
            }
            else
            {
                globalExtension = (Sein_imageBasedLightingExtension)entry.root.Extensions[ExtensionName];
            }

            var extension = new Sein_imageBasedLightingExtension();

            if (hasLighting && !hasReflection && _onlyLightingId.ContainsKey(entry))
            {
                extension.iblIndex = _onlyLightingId[entry];
                extension.iblType = 1;
                AddExtension(extensions, extension);
                return;
            }

            var light = new Sein_imageBasedLightingExtension.Light();

            var coefficients = new float[9][];
            UnityEngine.Rendering.SphericalHarmonicsL2 shs;
            LightProbes.GetInterpolatedProbe(new UnityEngine.Vector3(), null, out shs);
            float diffuseIntensity = 1;
            if (shs != null)
            {
                for (var c = 0; c < 9; c += 1)
                {
                    coefficients[c] = new float[3];
                    for (var b = 0; b < 3; b += 1)
                    {
                        coefficients[c][b] = shs[b, c];
                    }
                }
            }
            else
            {
                Debug.LogWarning("There is no baked light probe.");
            }

            light.shCoefficients = coefficients;
            light.diffuseIntensity = diffuseIntensity;
            light.brdfLUT = entry.SaveTexture(brdfLUT, false).Id;

            if (hasLighting && !hasReflection)
            {
                if (!_onlyLightingId.ContainsKey(entry))
                {
                    globalExtension.lights.Add(light);
                    _onlyLightingId.Add(entry, globalExtension.lights.Count - 1);
                }
                extension.iblIndex = _onlyLightingId[entry];
                extension.iblType = 1;
                AddExtension(extensions, extension);
                return;
            }

            var isCustomCubMap = RenderSettings.defaultReflectionMode == UnityEngine.Rendering.DefaultReflectionMode.Custom;
            Cubemap specMap;
            //ReflectionProbe
            float specIntensity = RenderSettings.reflectionIntensity;
            if (isCustomCubMap)
            {
                specMap = RenderSettings.customReflection;
            }
            else
            {
                var skybox = RenderSettings.skybox;

                if (skybox == null)
                {
                    Debug.LogWarning("Use skybox as relfection source, but skybox is not defined, ignore... Check 'http://seinjs.com/cn/tutorial/artist/reflection'");
                    return;
                }

                specMap = skybox.GetTexture("_Tex") as Cubemap;

                if (specMap == null)
                {
                    Debug.LogWarning("Use skybox as relfection source, but cubemap not set to skybox material, ignore... Check 'http://seinjs.com/cn/tutorial/artist/reflection'");
                    return;
                }
            }

            if (_cache.ContainsKey(entry) && _cache[entry].ContainsKey(specMap))
            {
                extension.iblIndex = _cache[entry][specMap];
                AddExtension(extensions, extension);
                return;
            }
            light.specIntensity = specIntensity;
            light.specMap = entry.SaveCubeTextureHDR(specMap, ExporterSettings.Lighting.reflectionType, ExporterSettings.Lighting.reflectionSize).Id;

            globalExtension.lights.Add(light);

            if (!_cache.ContainsKey(entry))
            {
                _cache[entry] = new Dictionary<Cubemap, int>();
            }

            _cache[entry].Add(specMap, globalExtension.lights.Count - 1);
            extension.iblIndex = _cache[entry][specMap];
            extension.iblType = 2;
            AddExtension(extensions, extension);
        }

        public override Extension Deserialize(GLTFRoot root, JProperty extensionToken)
        {
            return new Sein_imageBasedLightingExtension();
        }

        //https://forum.unity.com/threads/specular-convolution-when-calculating-mip-maps-for-cubemap-render-texture.617680/
        //private Cubemap GetSpecularCubeMap(Cubemap srcCubemap)
        //{
        //    var convolutionMaterial = new UnityEngine.Material(Shader.Find("Hidden/CubeBlur"));
        //    GL.PushMatrix();
        //    GL.LoadOrtho();
        //    var dstCubemap = new RenderTexture(srcCubemap.width, srcCubemap.height, 0, RenderTextureFormat.ARGBHalf);
        //    dstCubemap.dimension = UnityEngine.Rendering.TextureDimension.Cube;
        //    dstCubemap.volumeDepth = 6;
        //    dstCubemap.wrapMode = TextureWrapMode.Clamp;
        //    dstCubemap.filterMode = FilterMode.Trilinear;
        //    dstCubemap.isPowerOfTwo = true;
        //    dstCubemap.Create();
        //    // not support texture lod now
        //    var mip = 0;
        //    var dstMip = 0;
        //    var mipRes = srcCubemap.width;

        //    convolutionMaterial.SetTexture("_MainTex", srcCubemap);
        //    convolutionMaterial.SetFloat("_Texel", 1f / mipRes);
        //    convolutionMaterial.SetFloat("_Level", mip);

        //    convolutionMaterial.SetPass(0);

        //    // Positive X
        //    Graphics.SetRenderTarget(dstCubemap, dstMip, CubemapFace.PositiveX);
        //    GL.Begin(GL.QUADS);
        //    GL.TexCoord3(1, 1, 1);
        //    GL.Vertex3(0, 0, 1);
        //    GL.TexCoord3(1, -1, 1);
        //    GL.Vertex3(0, 1, 1);
        //    GL.TexCoord3(1, -1, -1);
        //    GL.Vertex3(1, 1, 1);
        //    GL.TexCoord3(1, 1, -1);
        //    GL.Vertex3(1, 0, 1);
        //    GL.End();

        //    // Negative X
        //    Graphics.SetRenderTarget(dstCubemap, dstMip, CubemapFace.NegativeX);
        //    GL.Begin(GL.QUADS);
        //    GL.TexCoord3(-1, 1, -1);
        //    GL.Vertex3(0, 0, 1);
        //    GL.TexCoord3(-1, -1, -1);
        //    GL.Vertex3(0, 1, 1);
        //    GL.TexCoord3(-1, -1, 1);
        //    GL.Vertex3(1, 1, 1);
        //    GL.TexCoord3(-1, 1, 1);
        //    GL.Vertex3(1, 0, 1);
        //    GL.End();

        //    // Positive Y
        //    Graphics.SetRenderTarget(dstCubemap, dstMip, CubemapFace.PositiveY);
        //    GL.Begin(GL.QUADS);
        //    GL.TexCoord3(-1, 1, -1);
        //    GL.Vertex3(0, 0, 1);
        //    GL.TexCoord3(-1, 1, 1);
        //    GL.Vertex3(0, 1, 1);
        //    GL.TexCoord3(1, 1, 1);
        //    GL.Vertex3(1, 1, 1);
        //    GL.TexCoord3(1, 1, -1);
        //    GL.Vertex3(1, 0, 1);
        //    GL.End();

        //    // Negative Y
        //    Graphics.SetRenderTarget(dstCubemap, dstMip, CubemapFace.NegativeY);
        //    GL.Begin(GL.QUADS);
        //    GL.TexCoord3(-1, -1, 1);
        //    GL.Vertex3(0, 0, 1);
        //    GL.TexCoord3(-1, -1, -1);
        //    GL.Vertex3(0, 1, 1);
        //    GL.TexCoord3(1, -1, -1);
        //    GL.Vertex3(1, 1, 1);
        //    GL.TexCoord3(1, -1, 1);
        //    GL.Vertex3(1, 0, 1);
        //    GL.End();

        //    // Positive Z
        //    Graphics.SetRenderTarget(dstCubemap, dstMip, CubemapFace.PositiveZ);
        //    GL.Begin(GL.QUADS);
        //    GL.TexCoord3(1, 1, -1);
        //    GL.Vertex3(0, 0, 1);
        //    GL.TexCoord3(1, -1, -1);
        //    GL.Vertex3(0, 1, 1);
        //    GL.TexCoord3(-1, -1, -1);
        //    GL.Vertex3(1, 1, 1);
        //    GL.TexCoord3(-1, 1, -1);
        //    GL.Vertex3(1, 0, 1);
        //    GL.End();

        //    // Negative Z
        //    Graphics.SetRenderTarget(dstCubemap, dstMip, CubemapFace.NegativeZ);
        //    GL.Begin(GL.QUADS);
        //    GL.TexCoord3(-1, 1, 1);
        //    GL.Vertex3(0, 0, 1);
        //    GL.TexCoord3(-1, -1, 1);
        //    GL.Vertex3(0, 1, 1);
        //    GL.TexCoord3(1, -1, 1);
        //    GL.Vertex3(1, 1, 1);
        //    GL.TexCoord3(1, 1, 1);
        //    GL.Vertex3(1, 0, 1);
        //    GL.End();

        //    GL.PopMatrix();

        //    Graphics.SetRenderTarget(null);

        //    return dstCubemap;
        //}

        private void DeleteTempMap(Cubemap map)
        {
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(map));
        }
    }
}
