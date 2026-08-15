using System;
using System.Collections;
using System.Collections.Generic;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

/// <summary>
/// Compatibility helpers that replicate resource-creation behavior found in newer UndertaleModLib
/// versions, so this app works with the UndertaleModLib checked into this repository.
/// </summary>
public static class UndertaleModLibCompatibility
{
    /// <summary>
    /// Creates a new resource based on the type of the list.
    /// </summary>
    public static UndertaleResource CreateResource(IList list)
    {
        Type resourceType = list.GetType().GetGenericArguments()[0];
        return (Activator.CreateInstance(resourceType) as UndertaleResource)!;
    }

    /// <summary>
    /// Gets the default name of a resource based on a list (e.g., sprite0).
    /// </summary>
    public static string? GetDefaultResourceName(IList list)
    {
        Type resourceType = list.GetType().GetGenericArguments()[0];
        if (resourceType == typeof(UndertaleTexturePageItem) ||
            resourceType == typeof(UndertaleEmbeddedAudio) ||
            resourceType == typeof(UndertaleEmbeddedTexture))
        {
            return null;
        }

        string typeName = resourceType.Name.Replace("Undertale", "").Replace("GameObject", "Object").ToLower();
        return typeName + list.Count;
    }

    /// <summary>
    /// Initializes a newly created resource.
    /// </summary>
    /// <returns>Any additional resources that were created as part of initialization (e.g., code for a new script).</returns>
    public static IList<UndertaleResource> InitializeResource(this UndertaleData data, UndertaleResource resource, IList list, string? resourceName)
    {
        IList<UndertaleResource> newResources = new List<UndertaleResource>();

        // Set up name
        if (resource is UndertaleNamedResource namedResource)
        {
            UndertaleString name = resource switch
            {
                // UTMT only names.
                UndertaleTexturePageItem => new UndertaleString("PageItem " + list.Count),
                UndertaleEmbeddedAudio => new UndertaleString("EmbeddedSound " + list.Count),
                UndertaleEmbeddedTexture => new UndertaleString("Texture " + list.Count),
                _ => data.Strings.MakeString(resourceName ?? "", createNew: true),
            };

            namedResource.Name = name;
        }

        if (resource is UndertaleString stringResource)
        {
            stringResource.Content = resourceName;
        }
        else if (resource is UndertaleRoom room)
        {
            if (data.IsVersionAtLeast(2))
            {
                room.Caption = null;
                room.Backgrounds.Clear();
                if (data.IsVersionAtLeast(2024, 13))
                {
                    room.Flags |= UndertaleRoom.RoomEntryFlags.IsGM2024_13;
                    room.InstanceCreationOrderIDs ??= new();
                }
                else
                {
                    room.Flags |= UndertaleRoom.RoomEntryFlags.IsGMS2;
                    if (data.IsVersionAtLeast(2, 3))
                    {
                        room.Flags |= UndertaleRoom.RoomEntryFlags.IsGMS2_3;
                    }
                }
            }
            else
            {
                room.Caption = data.Strings.MakeString("", createNew: true);
            }
        }
        else if (resource is UndertaleScript script)
        {
            if (data.IsVersionAtLeast(2, 3))
            {
                script.Code = UndertaleCode.CreateEmptyEntry(data, data.Strings.MakeString($"gml_GlobalScript_{script.Name.Content}", createNew: true));
                if (data.GlobalInitScripts is IList<UndertaleGlobalInit> globalInitScripts)
                {
                    globalInitScripts.Add(new UndertaleGlobalInit()
                    {
                        Code = script.Code,
                    });
                }
            }
            else
            {
                script.Code = UndertaleCode.CreateEmptyEntry(data, data.Strings.MakeString($"gml_Script_{script.Name.Content}", createNew: true));
            }

            newResources.Add(script.Code);
        }
        else if (resource is UndertaleCode code)
        {
            if (data.CodeLocals is not null)
            {
                code.LocalsCount = 1;
                UndertaleCodeLocals.CreateEmptyEntry(data, code.Name);
            }
            else
            {
                code.WeirdLocalFlag = true;
            }
        }
        else if (resource is UndertaleExtension)
        {
            if (data.GeneralInfo?.Major >= 2 ||
                (data.GeneralInfo?.Major == 1 && data.GeneralInfo?.Build >= 1773) ||
                (data.GeneralInfo?.Major == 1 && data.GeneralInfo?.Build == 1539))
            {
                byte[] newProductID = { 0xBA, 0x5E, 0xBA, 0x11, 0xBA, 0xDD, 0x06, 0x60, 0xBE, 0xEF, 0xED, 0xBA, 0x0B, 0xAB, 0xBA, 0xBE };
                data.FORM.EXTN.productIdData.Add(newProductID);
            }
        }
        else if (resource is UndertaleShader shader)
        {
            shader.GLSL_ES_Vertex = data.Strings.MakeString("", createNew: true);
            shader.GLSL_ES_Fragment = data.Strings.MakeString("", createNew: true);
            shader.GLSL_Vertex = data.Strings.MakeString("", createNew: true);
            shader.GLSL_Fragment = data.Strings.MakeString("", createNew: true);
            shader.HLSL9_Vertex = data.Strings.MakeString("", createNew: true);
            shader.HLSL9_Fragment = data.Strings.MakeString("", createNew: true);
        }

        return newResources;
    }

    /// <summary>
    /// Creates a shallow copy of a room game object instance, preserving all instance properties.
    /// </summary>
    public static UndertaleRoom.GameObject Clone(this UndertaleRoom.GameObject gameObject)
    {
        return new UndertaleRoom.GameObject()
        {
            X = gameObject.X,
            Y = gameObject.Y,
            ObjectDefinition = gameObject.ObjectDefinition,
            InstanceID = gameObject.InstanceID,
            CreationCode = gameObject.CreationCode,
            ScaleX = gameObject.ScaleX,
            ScaleY = gameObject.ScaleY,
            Color = gameObject.Color,
            Rotation = gameObject.Rotation,
            PreCreateCode = gameObject.PreCreateCode,
            ImageSpeed = gameObject.ImageSpeed,
            ImageIndex = gameObject.ImageIndex,
            Nonexistent = gameObject.Nonexistent,
        };
    }
}