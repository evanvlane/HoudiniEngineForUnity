﻿/*
* Copyright (c) <2020> Side Effects Software Inc.
* All rights reserved.
*
* Redistribution and use in source and binary forms, with or without
* modification, are permitted provided that the following conditions are met:
*
* 1. Redistributions of source code must retain the above copyright notice,
*    this list of conditions and the following disclaimer.
*
* 2. The name of Side Effects Software may not be used to endorse or
*    promote products derived from this software without specific prior
*    written permission.
*
* THIS SOFTWARE IS PROVIDED BY SIDE EFFECTS SOFTWARE "AS IS" AND ANY EXPRESS
* OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
* OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.  IN
* NO EVENT SHALL SIDE EFFECTS SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT,
* INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
* LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA,
* OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
* LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
* NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
* EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HoudiniEngineUnity
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // Typedefs (copy these from HEU_Common.cs)
    using HAPI_NodeId = System.Int32;
    using HAPI_PartId = System.Int32;
    using HAPI_ParmId = System.Int32;
    using HAPI_StringHandle = System.Int32;


    // Wrapper around Unity's Material, with some helper functions.
    public class HEU_MaterialData : ScriptableObject, IEquivable<HEU_MaterialData>
    {
	// Actual Unity material
	public Material _material;

	// Where the material originated from
	public enum Source
	{
	    DEFAULT,
	    HOUDINI,
	    UNITY,
	    SUBSTANCE,
	}
	public Source _materialSource;

	/// <summary>
	/// Returns true if this material was pre-existing in Unity and not generated from Houdini at cook time.
	/// </summary>
	public bool IsExistingMaterial() { return _materialSource == Source.UNITY || _materialSource == Source.SUBSTANCE; }

	// The ID generated by this plugin for managing on the Unity side.
	// All HEU_MaterialData will have a unique ID, either same as _materialHoudiniID for Houdini materials.
	// or hash of material path

	// The ID used to uniquely identify a material.
	// For Houdini materials, this is the ID returned by the material info.
	// For existing Unity materials (via unity_material attribute), this is 
	// the hash of the material path on project (eg. Assets/Materials/materialname.mat)
	public int _materialKey = HEU_Defines.HEU_INVALID_MATERIAL;


	/// <summary>
	/// For this object's _material, we update the shader attributes and 
	/// fetch the textures from Houdini.
	/// </summary>
	/// <param name="materialInfo">This material's info from Houdini</param>
	/// <param name="assetCacheFolderPath">Path to asset's cache folder</param>
	public void UpdateMaterialFromHoudini(HAPI_MaterialInfo materialInfo, string assetCacheFolderPath)
	{
	    if (_material == null)
	    {
		return;
	    }

	    HEU_SessionBase session = HEU_SessionManager.GetOrCreateDefaultSession();

	    HAPI_NodeInfo nodeInfo = new HAPI_NodeInfo();
	    if (!session.GetNodeInfo(materialInfo.nodeId, ref nodeInfo))
	    {
		return;
	    }

	    // Get all parameters of this material
	    HAPI_ParmInfo[] parmInfos = new HAPI_ParmInfo[nodeInfo.parmCount];
	    if (!HEU_GeneralUtility.GetArray1Arg(materialInfo.nodeId, session.GetParams, parmInfos, 0, nodeInfo.parmCount))
	    {
		return;
	    }

	    // Assign transparency shader or non-transparent.
	    if (IsTransparentMaterial(session, materialInfo.nodeId, parmInfos))
	    {
		_material.shader = HEU_MaterialFactory.FindPluginShader(HEU_PluginSettings.DefaultTransparentShader);
	    }
	    else
	    {
		_material.shader = HEU_MaterialFactory.FindPluginShader(HEU_PluginSettings.DefaultStandardShader);
	    }

	    // Diffuse texture - render & extract
	    int diffuseMapParmIndex = HEU_ParameterUtility.GetParameterIndexFromNameOrTag(session, nodeInfo.id, parmInfos, HEU_Defines.MAT_OGL_TEX1_ATTR);
	    if (diffuseMapParmIndex < 0)
	    {
		diffuseMapParmIndex = HEU_ParameterUtility.GetParameterIndexFromNameOrTag(session, nodeInfo.id, parmInfos, HEU_Defines.MAT_BASECOLOR_ATTR);
		if (diffuseMapParmIndex < 0)
		{
		    diffuseMapParmIndex = HEU_ParameterUtility.GetParameterIndexFromNameOrTag(session, nodeInfo.id, parmInfos, HEU_Defines.MAT_MAP_ATTR);
		}
	    }
	    if (diffuseMapParmIndex >= 0 && diffuseMapParmIndex < parmInfos.Length)
	    {
		string diffuseTextureFileName = GetTextureFileNameFromMaterialParam(session, materialInfo.nodeId, parmInfos[diffuseMapParmIndex]);
		_material.mainTexture = HEU_MaterialFactory.RenderAndExtractImageToTexture(session, materialInfo, parmInfos[diffuseMapParmIndex].id, diffuseTextureFileName, assetCacheFolderPath);
	    }

	    // Normal map - render & extract texture
	    int normalMapParmIndex = HEU_ParameterUtility.GetParameterIndexFromNameOrTag(session, nodeInfo.id, parmInfos, HEU_Defines.MAT_OGL_NORMAL_ATTR);
	    if (normalMapParmIndex >= 0 && normalMapParmIndex < parmInfos.Length)
	    {
		string normalTextureFileName = GetTextureFileNameFromMaterialParam(session, materialInfo.nodeId, parmInfos[normalMapParmIndex]);
		Texture2D normalMap = HEU_MaterialFactory.RenderAndExtractImageToTexture(session, materialInfo, parmInfos[normalMapParmIndex].id, normalTextureFileName, assetCacheFolderPath);
		if (normalMap != null)
		{
		    _material.SetTexture(HEU_Defines.UNITY_SHADER_BUMP_MAP, normalMap);
		}
	    }

	    // Assign shader properties

	    // Clamp shininess to non-zero as results in very hard shadows. Unity's UI does not allow zero either.
	    float shininess = HEU_ParameterUtility.GetParameterFloatValue(session, materialInfo.nodeId, parmInfos, HEU_Defines.MAT_OGL_ROUGH_ATTR, 0f);
	    _material.SetFloat(HEU_Defines.UNITY_SHADER_SHININESS, Mathf.Max(0.03f, 1.0f - shininess));

	    Color diffuseColor = HEU_ParameterUtility.GetParameterColor3Value(session, materialInfo.nodeId, parmInfos, HEU_Defines.MAT_OGL_DIFF_ATTR, Color.white);
	    diffuseColor.a = HEU_ParameterUtility.GetParameterFloatValue(session, materialInfo.nodeId, parmInfos, HEU_Defines.MAT_OGL_ALPHA_ATTR, 1f);
	    _material.SetColor(HEU_Defines.UNITY_SHADER_COLOR, diffuseColor);

	    Color specular = HEU_ParameterUtility.GetParameterColor3Value(session, materialInfo.nodeId, parmInfos, HEU_Defines.MAT_OGL_SPEC_ATTR, Color.black);
	    _material.SetColor(HEU_Defines.UNITY_SHADER_SPECCOLOR, specular);
	}

	/// <summary>
	/// Return the file name for the given material node's parameter.
	/// </summary>
	/// <param name="session">Current session</param>
	/// <param name="nodeID">Material node ID</param>
	/// <param name="parmInfo">Parameter on material to query</param>
	/// <returns>Given parameter's string value</returns>
	public static string GetTextureFileNameFromMaterialParam(HEU_SessionBase session, HAPI_NodeId nodeID, HAPI_ParmInfo parmInfo)
	{
	    string textureFileName = "default_texture.png";

	    HAPI_StringHandle stringValue;
	    string paramName = HEU_SessionManager.GetString(parmInfo.nameSH, session);
	    if (session.GetParmStringValue(nodeID, paramName, 0, true, out stringValue))
	    {
		string paramStrValue = HEU_SessionManager.GetString(stringValue, session);

		// The returned string needs to be cleaned up:
		// eg. opdef:/Sop/testgeometry_pighead?lowres.jpg -> Sop_testgeometry_pighead_lowres.jpg
		textureFileName = paramStrValue;

		int lastColon = textureFileName.LastIndexOf(':');
		if (lastColon > 0 && (lastColon + 1) < textureFileName.Length)
		{
		    textureFileName = textureFileName.Substring(lastColon + 1);
		}

		// Remove starting / after removing :: above
		textureFileName = textureFileName.TrimStart('/');

		textureFileName = textureFileName.Replace("?", "_");
		textureFileName = textureFileName.Replace("/", "_");

		//HEU_Logger.LogFormat("Texture File Name: {0}, {1}", paramStrValue, textureFileName);
	    }

	    return textureFileName;
	}

	/// <summary>
	/// Retruns true if the material (via its parameters) is a transparent material or not.
	/// </summary>
	/// <param name="session">Current Houdini session</param>
	/// <param name="nodeID">The material node ID</param>
	/// <param name="parameters">Parameter array containing material info</param>
	/// <returns>True if the material is transparent</returns>
	public static bool IsTransparentMaterial(HEU_SessionBase session, HAPI_NodeId nodeID, HAPI_ParmInfo[] parameters)
	{
	    float alpha = HEU_ParameterUtility.GetParameterFloatValue(session, nodeID, parameters, HEU_Defines.MAT_OGL_ALPHA_ATTR, 1f);
	    return alpha < 0.95f;
	}

	/// <summary>
	/// Returns null if the given image info supports a Unity friendly image format.
	/// Otherwise returns a file format that we know Unity supports.
	/// </summary>
	/// <param name="imageInfo">Image info containing the current image file format</param>
	/// <returns></returns>
	public static string GetSupportedFileFormat(HEU_SessionBase session, ref HAPI_ImageInfo imageInfo)
	{
	    string desiredFileFormatName = null;

	    string imageInfoFileFormat = HEU_SessionManager.GetString(imageInfo.imageFileFormatNameSH, session);

	    if (!imageInfoFileFormat.Equals(HEU_HAPIConstants.HAPI_PNG_FORMAT_NAME)
		    && !imageInfoFileFormat.Equals(HEU_HAPIConstants.HAPI_JPEG_FORMAT_NAME)
		    && !imageInfoFileFormat.Equals(HEU_HAPIConstants.HAPI_BMP_FORMAT_NAME)
		    && !imageInfoFileFormat.Equals(HEU_HAPIConstants.HAPI_TGA_FORMAT_NAME))
	    {
		desiredFileFormatName = HEU_HAPIConstants.HAPI_PNG_FORMAT_NAME;
	    }
	    return desiredFileFormatName;
	}

	public bool IsEquivalentTo(HEU_MaterialData other)
	{
	    bool bResult = true;

	    string header = "HEU_MaterialData";

	    if (other == null)
	    {
		HEU_Logger.LogError(header + " Not equivalent");
		return false;
	    }

	    HEU_TestHelpers.AssertTrueLogEquivalent(this._material.ToTestObject(), other._material.ToTestObject(), ref bResult, header, "_material");

	    HEU_TestHelpers.AssertTrueLogEquivalent(this._materialSource, other._materialSource, ref bResult, header, "_materialSource");

	    // Skip _materialKey
	 
	    return bResult;
	}

    }

}   // HoudiniEngineUnity