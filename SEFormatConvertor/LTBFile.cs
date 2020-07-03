﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SELib;
using SELib.Utilities;
using SevenZip;

namespace SEFormatConvertor
{
    class LTBFile
    {
        /// <summary>
        /// A list of bones, in order by index
        /// </summary>
        public Dictionary<byte, SEModelBone> Bones { get; private set; }
        /// <summary>
        /// A list of meshes, in order
        /// </summary>
        public List<SEModelMesh> Meshes { get; private set; }
        /// <summary>
        /// A list of materials, in order
        /// </summary>
        public List<SEModelMaterial> Materials { get; private set; }

        public Dictionary<int, List<int[]>> WeightSets { get; private set; }
        public Dictionary<int, List<float[]>> Weights { get; private set; }

        public Dictionary<string, SEAnim> Animations { get; private set; }

        public enum PieceType
        {
            RigidMesh = 4,
            SkelMesh = 5,
            VAMesh = 6
        }

        [Flags]
        enum DataType
        {
            Position = 0x0001,
            Normal = 0x0002,
            Color = 0x0004,
            UVSets1 = 0x0010,
            UVSets2 = 0x0020,
            UVSets3 = 0x0040,
            UVSets4 = 0x0080,
            BasisVectors = 0x0100
        }

        enum AnimCompressionType 
        { 
            None = 0, 
            Relevant = 1, 
            Relevant_16bit = 2, 
            REL_PV16 = 3 
        };

        private LTBFile()
        {
            Bones = new Dictionary<byte, SEModelBone>();
            Meshes = new List<SEModelMesh>();
            Materials = new List<SEModelMaterial>();

            WeightSets = new Dictionary<int, List<int[]>>();
            Weights = new Dictionary<int, List<float[]>>();

            Animations = new Dictionary<string, SEAnim>();
        }

        public SEModel ToSEModel()
        {
            var semdl = new SEModel();

            foreach (var bone in from x in Bones orderby x.Key ascending select x.Value)
                semdl.AddBone(bone.BoneName, bone.BoneParent, bone.GlobalPosition, bone.GlobalRotation, bone.LocalPosition, bone.LocalRotation, bone.Scale);

            foreach (var material in Materials)
                semdl.AddMaterial(material);

            foreach (var mesh in Meshes)
                semdl.AddMesh(mesh);

            semdl.GenerateLocalPositions(true, true);

            return semdl;
        }

        public static LTBFile Read(string path)
        {

            var ltbFile = new LTBFile();

            var br = new ExtendedBinaryReader(File.OpenRead(path));

            var header = br.ReadUInt16();
            if(header > 20)
            {
                br.Close();
                var lzmaStream = new LzmaDecodeStream(File.OpenRead(path));
                var ms = new MemoryStream();

                lzmaStream.CopyTo(ms);

                br = new ExtendedBinaryReader(ms);
            }

            uint version, numBones, numMesh;

            br.Skip(0x14, true);

            version = br.ReadUInt32();

            br.Skip(0x8);

            numBones = br.ReadUInt32();

            br.Skip(0x3A);

            numMesh = br.ReadUInt32();

            // Parse mesh nodes
            for (int i = 0; i < numMesh; i++)
            {
                string meshName = br.ReadStringWithUInt16Length().Replace('.', '_').Replace('-', '_').Replace(' ', '_');
                uint numLod = br.ReadUInt32();

                Console.WriteLine($"LTB Version {version}\n{meshName} - {numLod} Lods");

                br.Skip((int)numLod * 4 + 8);

                int materialIndex = -1;

                if (!ltbFile.Materials.Any(material => material.Name == meshName))
                {
                    ltbFile.Materials.Add(new SEModelMaterial
                    {
                        Name = "mtl_" + meshName,
                        MaterialData = new SEModelSimpleMaterial
                        {
                            DiffuseMap = meshName + ".png"
                        }
                    });

                    materialIndex = ltbFile.Materials.Count - 1;
                }
                else
                {
                    materialIndex = ltbFile.Materials.FindIndex(mtl => mtl.Name == meshName);
                }

                for (int iLod = 0; iLod < numLod; iLod ++)
                {
                    var mesh = new SEModelMesh();
                    mesh.AddMaterialIndex(materialIndex);

                    br.Skip(25);

                    var lodType = (PieceType)br.ReadUInt32();

                    var lodSize = br.ReadUInt32();
                    
                    if(lodSize != 0)
                    {
                        uint numVerts = br.ReadUInt32();
                        uint numTris = br.ReadUInt32();

                        uint iMaxBonesPerTri = br.ReadUInt32();
                        uint iMaxBonesPerVert = br.ReadUInt32();

                        Console.WriteLine($"    Lod {iLod}: \n        Vertex count: {numVerts}\n        Triangle count: {numTris}");

                        if(lodType == PieceType.SkelMesh)
                            br.Skip(1);

                        DataType[] streamData = { (DataType)br.ReadUInt32(), (DataType)br.ReadUInt32(), (DataType)br.ReadUInt32(), (DataType)br.ReadUInt32() };

                        uint rigidBone = uint.MaxValue;

                        if (lodType == PieceType.RigidMesh)
                            rigidBone = br.ReadUInt32();
                        else if (lodType == PieceType.SkelMesh)
                            br.Skip(1);
                        else throw new Exception("Unsupported lod type");

                        for (int iStream = 0; iStream < 4; ++iStream)
                        {
                            if (!streamData[iStream].HasFlag(DataType.Position))
                                continue;

                            for (int iVert = 0; iVert < numVerts; iVert++)
                            {
                                var v = new SEModelVertex();

                                if (streamData[iStream].HasFlag(DataType.Position))
                                {
                                    v.Position = new Vector3
                                    {
                                        X = br.ReadSingle(),
                                        Y = br.ReadSingle(),
                                        Z = br.ReadSingle(),
                                    };

                                    if (rigidBone == uint.MaxValue)
                                    {
                                        var weightSum = 0.0f;

                                        for (int iWeight = 0; iWeight < iMaxBonesPerTri - 1; iWeight++)
                                        {
                                            var weight = br.ReadSingle();

                                            if (weight > 1)
                                                throw new Exception("wtf");

                                            weightSum += weight;

                                            v.Weights.Add(new SEModelWeight
                                            {
                                                BoneIndex = uint.MaxValue,
                                                BoneWeight = weight
                                            });
                                        }

                                        if (1.0f - weightSum > float.Epsilon)
                                        {
                                            v.Weights.Add(new SEModelWeight
                                            {
                                                BoneIndex = uint.MaxValue,
                                                BoneWeight = 1.0f - weightSum
                                            });
                                        }
                                    }
                                    else
                                    {
                                        if (rigidBone >= numBones || rigidBone < 0)
                                            throw new Exception("wtf");

                                        v.Weights.Add(new SEModelWeight
                                        {
                                            BoneIndex = rigidBone,
                                            BoneWeight = 1.0f
                                        });
                                    }
                                }

                                if (streamData[iStream].HasFlag(DataType.Normal))
                                {
                                    v.VertexNormal = new Vector3
                                    {
                                        X = br.ReadSingle(),
                                        Y = br.ReadSingle(),
                                        Z = br.ReadSingle(),
                                    };
                                }

                                if (streamData[iStream].HasFlag(DataType.Color))
                                    br.Skip(4);

                                if (streamData[iStream].HasFlag(DataType.UVSets1))
                                {
                                    v.UVSets.Add(new Vector2
                                    {
                                        X = br.ReadSingle(),
                                        Y = 1.0f - br.ReadSingle()
                                    });

                                    if (v.UVSets[0].X > 1.0f)
                                        v.UVSets[0].X -= 1.0f;
                                }

                                if (streamData[iStream].HasFlag(DataType.UVSets2))
                                    br.Skip(8);
                                if (streamData[iStream].HasFlag(DataType.UVSets3))
                                    br.Skip(8);
                                if (streamData[iStream].HasFlag(DataType.UVSets4))
                                    br.Skip(8);
                                if (streamData[iStream].HasFlag(DataType.BasisVectors))
                                    br.Skip(24);

                                if (v.Position == null || v.WeightCount == 0)
                                    throw new Exception("wtf");

                                mesh.AddVertex(v);
                            }
                        }

                        for(uint iTriangle = 0; iTriangle < numTris; iTriangle ++)
                            mesh.AddFace(br.ReadUInt16(), br.ReadUInt16(), br.ReadUInt16());
                        
                        if(lodType == PieceType.SkelMesh)
                        {
                            var boneComboCount = br.ReadUInt32();

                            for(int iCombo = 0; iCombo < boneComboCount; iCombo ++)
                            {
                                int m_BoneIndex_Start = br.ReadUInt16();
                                int m_BoneIndex_End = m_BoneIndex_Start + br.ReadUInt16();

                                Console.WriteLine($"        Weight Combo: {m_BoneIndex_Start} to {m_BoneIndex_End}");

                                var bones = br.ReadBytes(4);

                                uint m_iIndexIndex = br.ReadUInt32();

                                for(int iVertex = m_BoneIndex_Start; iVertex < m_BoneIndex_End; iVertex++)
                                {
                                    for(int iBone = 0; iBone < 4 && bones[iBone] != 0xFF; iBone ++)
                                    {
                                        if (mesh.Verticies[iVertex].Weights.Count <= iBone)
                                            break;

                                        mesh.Verticies[iVertex].Weights[iBone].BoneIndex = bones[iBone];
                                    }
                                }
                            }
                        }

                        ltbFile.Meshes.Add(mesh);
                    }

                    br.Skip(br.ReadByte());
                }
            }

            uint[] boneTree = new uint[numBones];

            for (int i = 0; i < numBones; i++)
            {
                var boneName = br.ReadStringWithUInt16Length();
                var boneId = br.ReadByte();
                var num2 = br.ReadUInt16();

                double[,] transformMatrix = new double[4, 4];

                for (long j = 0; j < 4; j++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        transformMatrix[j, k] = br.ReadSingle();
                    }
                }

                boneTree[i] = br.ReadUInt32();

                var bone = new SEModelBone
                {
                    BoneName = boneName.Replace('.', '_').Replace('-', '_').Replace(' ', '_'),
                    GlobalRotation = Quaternion.FromMatrix4x4(transformMatrix),
                    GlobalPosition = Vector3.FromMatrix4x4(transformMatrix)
                };

                ltbFile.Bones[boneId] = bone;
            }

            uint[] nSubbone = new uint[numBones];
            nSubbone[0] = boneTree[0];

            ltbFile.Bones[0].BoneParent = -1;

            // Build bone tree
            for (byte i = 1; i < numBones; i++)
            {
                nSubbone[i] = boneTree[i];
                for (int j = i - 1; j >= 0; j--)
                {
                    if(nSubbone[j] > 0)
                    {
                        nSubbone[j]--;
                        ltbFile.Bones[i].BoneParent = j;
                        break;
                    }
                }
            }

            foreach (var mesh in ltbFile.Meshes)
            {
                foreach (var v in mesh.Verticies)
                {
                    for(int i = (int)v.WeightCount - 1; i >= 0; i--)
                    {
                        if (v.Weights[i].BoneWeight == 0)
                        {
                            //Console.WriteLine($"Removed SB Weight at vertex {mesh.Verticies.IndexOf(v)} on mesh {ltbFile.Meshes.IndexOf(mesh)}");
                            v.Weights.RemoveAt(i);
                        }
                    }
                }
            }

            Console.WriteLine("\nInternal filenames:");
            var childModelCount = br.ReadUInt32();

            for(int i = 0; i < childModelCount; i++)
            {
                Console.WriteLine(br.ReadStringWithUInt16Length());

                br.Skip((int)br.ReadUInt32() * 4);
            }

            br.Skip(4);

            if (br.BaseStream.Length - br.BaseStream.Position > 2048)
            {
                var animationCount = br.ReadUInt32();

                Console.WriteLine($"\nAnimation count: {animationCount}\n");

                for (int i = 0; i < animationCount; i++)
                {
                    var seanim = new SEAnim();

                    var dim = new Vector3
                    {
                        X = br.ReadSingle(),
                        Y = br.ReadSingle(),
                        Z = br.ReadSingle(),
                    };

                    var animName = br.ReadStringWithUInt16Length();
                    Console.Write(animName);

                    var compressionType = (AnimCompressionType)br.ReadUInt32();
                    var interpolationMS = br.ReadUInt32();

                    var keyFrameCount = br.ReadUInt32();
                    Console.WriteLine($" has {keyFrameCount} keyframes");

                    for (int iKeyFrame = 0; iKeyFrame < keyFrameCount; iKeyFrame++)
                    {
                        var time = br.ReadUInt32();
                        var animString = br.ReadStringWithUInt16Length();

                        if (!string.IsNullOrEmpty(animString))
                            seanim.AddNoteTrack(animString, iKeyFrame);
                    }

                    for (byte iBone = 0; iBone < numBones; iBone++)
                    {
                        if(compressionType != AnimCompressionType.None)
                        {
                            int pFrames = br.ReadInt32();

                            for(int iKeyFrame = 0; iKeyFrame < pFrames; iKeyFrame++)
                            {
                                seanim.AddTranslationKey(ltbFile.Bones[iBone].BoneName, iKeyFrame, br.ReadInt16() / 16.0, br.ReadInt16() / 16.0, br.ReadInt16() / 16.0);
                            }

                            int rFrames = br.ReadInt32();
                            
                            for(int iKeyFrame = 0; iKeyFrame < rFrames; iKeyFrame++)
                            {
                                seanim.AddRotationKey(ltbFile.Bones[iBone].BoneName, iKeyFrame, br.ReadInt16() / 16.0, br.ReadInt16() / 16.0, br.ReadInt16() / 16.0, br.ReadInt16() / 16.0);
                            }
                        }
                        else if (compressionType == AnimCompressionType.None)
                        {
                            bool isVertexAnim = br.ReadBoolean();

                            if (isVertexAnim)
                            {
                                throw new Exception("Vertex animation not supported!");
                            }
                            else
                            {
                                for (int iKeyFrame = 0; iKeyFrame < keyFrameCount; iKeyFrame++)
                                    seanim.AddTranslationKey(ltbFile.Bones[iBone].BoneName, iKeyFrame, br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                                for (int iKeyFrame = 0; iKeyFrame < keyFrameCount; iKeyFrame++)
                                    seanim.AddRotationKey(ltbFile.Bones[iBone].BoneName, iKeyFrame, br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            }
                        }
                    }

                    ltbFile.Animations.Add(animName + ".seanim", seanim);
                }
            }

            return ltbFile;
        }
    }
}