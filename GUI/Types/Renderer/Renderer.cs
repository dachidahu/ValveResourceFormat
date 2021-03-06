﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using GUI.Utils;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.Blocks.ResourceEditInfoStructs;
using ValveResourceFormat.KeyValues;
using ValveResourceFormat.ResourceTypes;
using Timer = System.Timers.Timer;

namespace GUI.Types.Renderer
{
    internal class Renderer
    {
        private readonly MaterialLoader MaterialLoader;
        private readonly TabControl tabs;

        private readonly Package CurrentPackage;
        private readonly string CurrentFileName;

        private readonly List<MeshObject> MeshesToRender;

        private bool Loaded;

        private GLControl meshControl;
        private Label cameraLabel;
        private Label fpsLabel;

        private CheckedListBox cameraBox;
        private List<Tuple<string, Matrix4>> cameras;

        private Camera ActiveCamera;

        private Vector3 MinBounds;
        private Vector3 MaxBounds;

        private int MaxTextureMaxAnisotropy;

        public Renderer(TabControl mainTabs, string fileName, Package currentPackage)
        {
            MeshesToRender = new List<MeshObject>();
            cameras = new List<Tuple<string, Matrix4>>();

            CurrentPackage = currentPackage;
            CurrentFileName = fileName;
            tabs = mainTabs;

            MaterialLoader = new MaterialLoader(CurrentFileName, CurrentPackage);
        }

        public void AddMeshObject(MeshObject obj)
        {
            MeshesToRender.Add(obj);
        }

        public Control CreateGL()
        {
            var panel = new Panel();
            panel.Dock = DockStyle.Fill;

            cameraLabel = new Label();
            cameraLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cameraLabel.AutoSize = true;
            cameraLabel.Dock = DockStyle.Top;
            panel.Controls.Add(cameraLabel);

            fpsLabel = new Label();
            fpsLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            fpsLabel.AutoSize = true;
            fpsLabel.Dock = DockStyle.Top;
            panel.Controls.Add(fpsLabel);

            cameraBox = new CheckedListBox();
            cameraBox.Width *= 2;
            cameraBox.Dock = DockStyle.Left;
            cameraBox.ItemCheck += CameraBox_ItemCheck;
            cameraBox.CheckOnClick = true;
            panel.Controls.Add(cameraBox);

#if DEBUG
            meshControl = new GLControl(new GraphicsMode(32, 24, 0, 8), 3, 3, GraphicsContextFlags.Debug);
#else
            meshControl = new GLControl(new GraphicsMode(32, 24, 0, 8), 3, 3, GraphicsContextFlags.Default);
#endif
            meshControl.Dock = DockStyle.Fill;
            meshControl.AutoSize = true;
            meshControl.Load += MeshControl_Load;
            meshControl.Paint += MeshControl_Paint;
            meshControl.Resize += MeshControl_Resize;
            meshControl.MouseEnter += MeshControl_MouseEnter;
            meshControl.MouseLeave += MeshControl_MouseLeave;
            meshControl.GotFocus += MeshControl_GotFocus;

            panel.Controls.Add(meshControl);
            return panel;
        }

        private void CameraBox_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            //https://social.msdn.microsoft.com/Forums/windows/en-US/5333cdf2-a669-467c-99ae-1530e91da43a/checkedlistbox-allow-only-one-item-to-be-selected?forum=winforms
            if (e.NewValue == CheckState.Checked)
            {
                for (int ix = 0; ix < cameraBox.Items.Count; ++ix)
                {
                    if (e.Index != ix)
                    {
                        cameraBox.ItemCheck -= CameraBox_ItemCheck;
                        cameraBox.SetItemChecked(ix, false);
                        cameraBox.ItemCheck += CameraBox_ItemCheck;
                    }
                }
                ActiveCamera = cameraBox.Items[e.Index] as Camera;
            }
            else if (e.CurrentValue == CheckState.Checked && cameraBox.CheckedItems.Count == 1)
            {
                e.NewValue = CheckState.Checked;
            }
        }

        private void MeshControl_GotFocus(object sender, EventArgs e)
        {
            meshControl.MakeCurrent();
            meshControl.SwapBuffers();
            meshControl.VSync = true;
        }

        private void MeshControl_MouseLeave(object sender, EventArgs e)
        {
            ActiveCamera.MouseOverRenderArea = false;
        }

        private void MeshControl_MouseEnter(object sender, EventArgs e)
        {
            ActiveCamera.MouseOverRenderArea = true;
        }

        private void MeshControl_Resize(object sender, EventArgs e)
        {
            if (!Loaded)
            {
                return;
            }

            ActiveCamera.SetViewportSize(tabs.Width, tabs.Height);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            meshControl.SwapBuffers();
        }

        private void InitializeInputTick()
        {
            var timer = new Timer();
            timer.Enabled = true;
            timer.Interval = 1000 / 60;
            timer.Elapsed += InputTick;
            timer.Start();
        }

        private void InputTick(object sender, EventArgs e)
        {
            ActiveCamera.HandleInput(Mouse.GetState(), Keyboard.GetState());
        }

        public void CheckOpenGL()
        {
            var extensions = new Dictionary<string, bool>();
            var count = GL.GetInteger(GetPName.NumExtensions);
            for (var i = 0; i < count; i++)
            {
                var extension = GL.GetString(StringNameIndexed.Extensions, i);
                extensions.Add(extension, true);
            }

            if (extensions.ContainsKey("GL_EXT_texture_filter_anisotropic"))
            {
                MaxTextureMaxAnisotropy = GL.GetInteger((GetPName)ExtTextureFilterAnisotropic.MaxTextureMaxAnisotropyExt);
            }
            else
            {
                Console.Error.WriteLine("GL_EXT_texture_filter_anisotropic is not supported");
            }
        }

        private void MeshControl_Load(object sender, EventArgs e)
        {
            meshControl.MakeCurrent();

            Console.WriteLine("OpenGL version: " + GL.GetString(StringName.Version));
            Console.WriteLine("OpenGL vendor: " + GL.GetString(StringName.Vendor));
            Console.WriteLine("GLSL version: " + GL.GetString(StringName.ShadingLanguageVersion));

            CheckOpenGL();
            LoadBoundingBox();

            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);

            GL.ClearColor(Settings.BackgroundColor);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            InitializeInputTick();

            ActiveCamera = new Camera(tabs.Width, tabs.Height, MinBounds, MaxBounds);
            cameraBox.Items.Add(ActiveCamera, true);
            foreach (var cameraInfo in cameras)
            {
                var camera = new Camera(tabs.Width, tabs.Height, cameraInfo.Item2, cameraInfo.Item1);
                cameraBox.Items.Add(camera);
            }

            foreach (var obj in MeshesToRender)
            {
                var resource = obj.Resource;
                var block = resource.VBIB;
                var data = (BinaryKV3)resource.Blocks[BlockType.DATA];
                var modelArguments = (ArgumentDependencies)((ResourceEditInfo)resource.Blocks[BlockType.REDI]).Structs[ResourceEditInfo.REDIStruct.ArgumentDependencies];

                var vertexBuffers = new uint[block.VertexBuffers.Count];
                var indexBuffers = new uint[block.IndexBuffers.Count];

                GL.GenBuffers(block.VertexBuffers.Count, vertexBuffers);
                GL.GenBuffers(block.IndexBuffers.Count, indexBuffers);

                for (var i = 0; i < block.VertexBuffers.Count; i++)
                {
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffers[i]);
                    GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(block.VertexBuffers[i].Count * block.VertexBuffers[i].Size), block.VertexBuffers[i].Buffer, BufferUsageHint.StaticDraw);

                    var verticeBufferSize = 0;
                    GL.GetBufferParameter(BufferTarget.ArrayBuffer, BufferParameterName.BufferSize, out verticeBufferSize);
                }

                for (var i = 0; i < block.IndexBuffers.Count; i++)
                {
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffers[i]);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(block.IndexBuffers[i].Count * block.IndexBuffers[i].Size), block.IndexBuffers[i].Buffer, BufferUsageHint.StaticDraw);

                    var indiceBufferSize = 0;
                    GL.GetBufferParameter(BufferTarget.ElementArrayBuffer, BufferParameterName.BufferSize, out indiceBufferSize);
                }

                //Prepare drawcalls
                var a = (KVObject)data.Data.Properties["m_sceneObjects"].Value;

                for (var b = 0; b < a.Properties.Count; b++)
                {
                    var c = (KVObject)((KVObject)a.Properties[b.ToString()].Value).Properties["m_drawCalls"].Value;

                    for (var i = 0; i < c.Properties.Count; i++)
                    {
                        var d = (KVObject)c.Properties[i.ToString()].Value;

                        string material = d.Properties["m_material"].Value.ToString();

                        if (obj.SkinMaterials.Any())
                        {
                            material = obj.SkinMaterials[i];
                        }

                        // TODO: Don't pass around so much shit
                        var drawCall = CreateDrawCall(d.Properties, vertexBuffers, indexBuffers, modelArguments, resource.VBIB, material);
                        obj.DrawCalls.Add(drawCall);
                    }
                }

                obj.DrawCalls = obj.DrawCalls.OrderBy(x => x.Material.Name).ToList();

                obj.Resource = null;
            }

            // TODO: poor hack
            FileExtensions.ClearCache();

            Loaded = true;

            Console.WriteLine("{0} draw calls total", MeshesToRender.Sum(x => x.DrawCalls.Count));
        }

        public void AddCamera(string name, Matrix4 megaMatrix)
        {
            Console.WriteLine($"Adding Camera {name} with matrix {megaMatrix}");
            cameras.Add(new Tuple<string, Matrix4>(name, megaMatrix));
        }

        private DrawCall CreateDrawCall(Dictionary<string, KVValue> drawProperties, uint[] vertexBuffers, uint[] indexBuffers, ArgumentDependencies modelArguments, VBIB block, string material)
        {
            var drawCall = new DrawCall();

            switch (drawProperties["m_nPrimitiveType"].Value.ToString())
            {
                case "RENDER_PRIM_TRIANGLES":
                    drawCall.PrimitiveType = PrimitiveType.Triangles;
                    break;
                default:
                    throw new Exception("Unknown PrimitiveType in drawCall! (" + drawProperties["m_nPrimitiveType"].Value + ")");
            }

            drawCall.Material = MaterialLoader.GetMaterial(material, MaxTextureMaxAnisotropy);

            // Load shader
            drawCall.Shader = ShaderLoader.LoadShader(drawCall.Material.ShaderName, modelArguments);

            //Bind and validate shader
            GL.UseProgram(drawCall.Shader.Program);

            var f = (KVObject)drawProperties["m_indexBuffer"].Value;

            var indexBuffer = default(DrawBuffer);
            indexBuffer.Id = Convert.ToUInt32(f.Properties["m_hBuffer"].Value);
            indexBuffer.Offset = Convert.ToUInt32(f.Properties["m_nBindOffsetBytes"].Value);
            drawCall.IndexBuffer = indexBuffer;

            var bufferSize = block.IndexBuffers[(int)drawCall.IndexBuffer.Id].Size;
            drawCall.BaseVertex = Convert.ToUInt32(drawProperties["m_nBaseVertex"].Value);
            drawCall.VertexCount = Convert.ToUInt32(drawProperties["m_nVertexCount"].Value);
            drawCall.StartIndex = Convert.ToUInt32(drawProperties["m_nStartIndex"].Value) * bufferSize;
            drawCall.IndexCount = Convert.ToInt32(drawProperties["m_nIndexCount"].Value);

            if (drawProperties.ContainsKey("m_vTintColor"))
            {
                var tint = (KVObject) drawProperties["m_vTintColor"].Value;
                drawCall.TintColor = new Vector3(
                    Convert.ToSingle(tint.Properties["0"].Value),
                    Convert.ToSingle(tint.Properties["1"].Value),
                    Convert.ToSingle(tint.Properties["2"].Value)
                );
            }

            if (bufferSize == 2)
            {
                //shopkeeper_vr
                drawCall.IndiceType = DrawElementsType.UnsignedShort;
            }
            else if (bufferSize == 4)
            {
                //glados
                drawCall.IndiceType = DrawElementsType.UnsignedInt;
            }
            else
            {
                throw new Exception("Unsupported indice type");
            }

            var g = (KVObject)drawProperties["m_vertexBuffers"].Value;
            var h = (KVObject)g.Properties["0"].Value; // TODO: Not just 0

            var vertexBuffer = default(DrawBuffer);
            vertexBuffer.Id = Convert.ToUInt32(h.Properties["m_hBuffer"].Value);
            vertexBuffer.Offset = Convert.ToUInt32(h.Properties["m_nBindOffsetBytes"].Value);
            drawCall.VertexBuffer = vertexBuffer;

            GL.GenVertexArrays(1, out drawCall.VertexArrayObject);

            GL.BindVertexArray(drawCall.VertexArrayObject);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffers[drawCall.VertexBuffer.Id]);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffers[drawCall.IndexBuffer.Id]);

            var curVertexBuffer = block.VertexBuffers[(int)drawCall.VertexBuffer.Id];
            var texCoordNum = 0;
            foreach (var attribute in curVertexBuffer.Attributes)
            {
                var attributeName = "v" + attribute.Name;

                // TODO: other params too?
                if (attribute.Name == "TEXCOORD" && texCoordNum++ > 0)
                {
                    attributeName += texCoordNum;
                }

                BindVertexAttrib(attribute, attributeName, drawCall.Shader.Program, (int)curVertexBuffer.Size);
            }

            GL.BindVertexArray(0);

            return drawCall;
        }

        private void MeshControl_Paint(object sender, PaintEventArgs e)
        {
            if (!Loaded)
            {
                return;
            }

            var fps = fpsLabel.Text;
            ActiveCamera.Tick(ref fps);
            fpsLabel.Text = fps;

            cameraLabel.Text = $"{ActiveCamera.Location.X}, {ActiveCamera.Location.Y}, {ActiveCamera.Location.Z}\n(yaw: {ActiveCamera.Yaw})";

            //Animate light position
            var lightPos = ActiveCamera.Location;
            var cameraLeft = new Vector3((float)Math.Cos(ActiveCamera.Yaw + MathHelper.PiOver2), (float)Math.Sin(ActiveCamera.Yaw + MathHelper.PiOver2), 0);
            lightPos += cameraLeft * 200 * (float)Math.Sin(Environment.TickCount / 500.0);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var prevShader = -1;
            var prevMaterial = string.Empty;
            var objChanged = false;
            int uniformLocation;

            //var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var obj in MeshesToRender)
            {
                objChanged = true;

                foreach (var call in obj.DrawCalls)
                {
                    if (call.Shader.Program != prevShader)
                    {
                        objChanged = true;
                        prevShader = call.Shader.Program;

                        GL.UseProgram(call.Shader.Program);

                        uniformLocation = call.Shader.GetUniformLocation("projection");
                        GL.UniformMatrix4(uniformLocation, false, ref ActiveCamera.ProjectionMatrix);

                        uniformLocation = call.Shader.GetUniformLocation("modelview");
                        GL.UniformMatrix4(uniformLocation, false, ref ActiveCamera.CameraViewMatrix);

                        uniformLocation = call.Shader.GetUniformLocation("vLightPosition");
                        GL.Uniform3(uniformLocation, lightPos);

                        uniformLocation = call.Shader.GetUniformLocation("vEyePosition");
                        GL.Uniform3(uniformLocation, ActiveCamera.Location);
                    }

                    // Stupidly hacky
                    if (objChanged)
                    {
                        objChanged = false;
                        prevMaterial = string.Empty;

                        var transform = obj.Transform;
                        uniformLocation = call.Shader.GetUniformLocation("transform");
                        GL.UniformMatrix4(uniformLocation, false, ref transform);

                        uniformLocation = call.Shader.GetUniformLocation("m_vTintColorSceneObject");
                        if (uniformLocation > -1)
                        {
                            GL.Uniform4(uniformLocation, obj.TintColor);
                        }
                    }

                    GL.BindVertexArray(call.VertexArrayObject);

                    uniformLocation = call.Shader.GetUniformLocation("m_vTintColorDrawCall");
                    if (uniformLocation > -1)
                    {
                        GL.Uniform3(uniformLocation, call.TintColor);
                    }

                    if (call.Material.Name != prevMaterial)
                    {
                        prevMaterial = call.Material.Name;

                        var textureUnit = 0;
                        foreach (var texture in call.Material.Textures)
                        {
                            TryToBindTexture(call.Shader.Program, textureUnit++, texture.Key, texture.Value);
                        }

                        foreach (var param in call.Material.FloatParams)
                        {
                            uniformLocation = call.Shader.GetUniformLocation(param.Key);

                            if (uniformLocation > -1)
                            {
                                GL.Uniform1(uniformLocation, param.Value);
                            }
                        }

                        foreach (var param in call.Material.VectorParams)
                        {
                            uniformLocation = call.Shader.GetUniformLocation(param.Key);

                            if (uniformLocation > -1)
                            {
                                GL.Uniform4(uniformLocation, param.Value);
                            }
                        }

                        var alpha = 0f;
                        if (call.Material.IntParams.ContainsKey("F_ALPHA_TEST") &&
                            call.Material.IntParams["F_ALPHA_TEST"] == 1 &&
                            call.Material.FloatParams.ContainsKey("g_flAlphaTestReference"))
                        {
                            alpha = call.Material.FloatParams["g_flAlphaTestReference"];
                        }

                        var alphaReference = call.Shader.GetUniformLocation("g_flAlphaTestReference");
                        GL.Uniform1(alphaReference, alpha);

                        /*
                        if (call.Material.IntParams.ContainsKey("F_TRANSLUCENT") && call.Material.IntParams["F_TRANSLUCENT"] == 1)
                        {
                            GL.Enable(EnableCap.Blend);
                            GL.BlendFunc(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
                        }
                        else
                        {
                            GL.Disable(EnableCap.Blend);
                        }
                        */
                    }

                    GL.DrawElements(call.PrimitiveType, call.IndexCount, call.IndiceType, (IntPtr)call.StartIndex);
                }
            }

            //sw.Stop(); Console.WriteLine("{0} {1} {2}", sw.Elapsed, sw.ElapsedTicks);

            // Only needed when debugging if something doesnt work, causes high CPU
            /*
            var error = GL.GetError();

            if (error != ErrorCode.NoError)
            {
                Console.WriteLine(error);
            }
            */

            meshControl.SwapBuffers();
            meshControl.Invalidate();
        }

        private void TryToBindTexture(int shader, int textureUnit, string uniform, int textureID)
        {
            //Get uniform location from the shader
            var uniformLocation = GL.GetUniformLocation(shader, uniform);

            //Stop if the uniform loction does not exist
            if (uniformLocation == -1)
            {
                return;
            }

            //Bind texture unit and texture
            GL.ActiveTexture(TextureUnit.Texture0 + textureUnit);
            GL.BindTexture(TextureTarget.Texture2D, textureID);

            //Set uniform location
            GL.Uniform1(uniformLocation, textureUnit);
        }

        // TODO: we're taking boundaries of first scene
        private void LoadBoundingBox()
        {
            var yo = MeshesToRender.FirstOrDefault();
            if (yo == null)
            {
                return;
            }

            var data = (BinaryKV3)yo.Resource.Blocks[BlockType.DATA];
            var a = (KVObject)data.Data.Properties["m_sceneObjects"].Value;
            var b = (KVObject)a.Properties["0"].Value;
            var minBounds = (KVObject)b.Properties["m_vMinBounds"].Value;
            var maxBounds = (KVObject)b.Properties["m_vMaxBounds"].Value;

            MaxBounds.X = (float)Convert.ToDouble(maxBounds.Properties["0"].Value);
            MinBounds.X = (float)Convert.ToDouble(minBounds.Properties["0"].Value);
            MaxBounds.Y = (float)Convert.ToDouble(maxBounds.Properties["1"].Value);
            MinBounds.Y = (float)Convert.ToDouble(minBounds.Properties["1"].Value);
            MaxBounds.Z = (float)Convert.ToDouble(maxBounds.Properties["2"].Value);
            MinBounds.Z = (float)Convert.ToDouble(minBounds.Properties["2"].Value);
        }

        private void BindVertexAttrib(VBIB.VertexAttribute attribute, string attributeName, int shaderProgram, int stride)
        {
            var attributeLocation = GL.GetAttribLocation(shaderProgram, attributeName);

            //Ignore this attribute if it is not found in the shader
            if (attributeLocation == -1)
            {
                return;
            }

            GL.EnableVertexAttribArray(attributeLocation);

            switch (attribute.Type)
            {
                case DXGI_FORMAT.R32G32B32_FLOAT:
                    GL.VertexAttribPointer(attributeLocation, 3, VertexAttribPointerType.Float, false, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R8G8B8A8_UNORM:
                    GL.VertexAttribPointer(attributeLocation, 4, VertexAttribPointerType.UnsignedByte, false, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R32G32_FLOAT:
                    GL.VertexAttribPointer(attributeLocation, 2, VertexAttribPointerType.Float, false, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R16G16_FLOAT:
                    GL.VertexAttribPointer(attributeLocation, 2, VertexAttribPointerType.HalfFloat, false, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R32G32B32A32_FLOAT:
                    GL.VertexAttribPointer(attributeLocation, 4, VertexAttribPointerType.Float, false, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R8G8B8A8_UINT:
                    GL.VertexAttribIPointer(attributeLocation, 4, VertexAttribIntegerType.UnsignedInt, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R16G16_SINT:
                    GL.VertexAttribIPointer(attributeLocation, 2, VertexAttribIntegerType.Short, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R16G16B16A16_SINT:
                    GL.VertexAttribIPointer(attributeLocation, 4, VertexAttribIntegerType.Short, stride, (IntPtr)attribute.Offset);
                    break;

                case DXGI_FORMAT.R16G16_UNORM:
                    GL.VertexAttribPointer(attributeLocation, 2, VertexAttribPointerType.UnsignedShort, true, stride, (IntPtr)attribute.Offset);
                    break;

                default:
                    throw new Exception("Unknown attribute format " + attribute.Type);
            }
        }
    }
}
