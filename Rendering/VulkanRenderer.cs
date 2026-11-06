using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Epsilon.LinearAlgebra;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace VectorField.Rendering;

public sealed unsafe class VulkanRenderer : IDisposable
{
    const int FramesInFlight = 2;
    const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    struct HostBuffer
    {
        public VkBuffer Buffer;
        public DeviceMemory Memory;
        public void* Mapped;
        public ulong Size;
    }

    readonly IWindow window;
    readonly Vk vk = Vk.GetApi();
    readonly bool validation;

    Instance instance;
    ExtDebugUtils? debugUtils;
    DebugUtilsMessengerEXT debugMessenger;
    PfnDebugUtilsMessengerCallbackEXT debugCallback;
    KhrSurface khrSurface = null!;
    SurfaceKHR surface;
    PhysicalDevice physicalDevice;
    Device device;
    uint graphicsFamily, presentFamily;
    Queue graphicsQueue, presentQueue;
    KhrSwapchain khrSwapchain = null!;

    SwapchainKHR swapchain;
    VkImage[] swapImages = [];
    ImageView[] swapViews = [];
    Framebuffer[] framebuffers = [];
    VkSemaphore[] renderFinished = [];
    SurfaceFormatKHR surfaceFormat;
    Extent2D extent;
    bool canCapture;

    SampleCountFlags samples;
    VkImage msaaImage;
    DeviceMemory msaaMemory;
    ImageView msaaView;

    RenderPass renderPass;
    PipelineLayout pipelineLayout;
    Pipeline alphaPipeline, additivePipeline;
    CommandPool commandPool;
    readonly CommandBuffer[] commandBuffers = new CommandBuffer[FramesInFlight];
    readonly VkSemaphore[] imageAvailable = new VkSemaphore[FramesInFlight];
    readonly Fence[] inFlight = new Fence[FramesInFlight];
    readonly HostBuffer[] vertexBuffers = new HostBuffer[FramesInFlight];
    readonly HostBuffer[] indexBuffers = new HostBuffer[FramesInFlight];
    int frame;
    bool swapchainDirty;

    public string DeviceName { get; private set; } = "";
    public System.IO.Compression.CompressionLevel CaptureCompression { get; set; } = System.IO.Compression.CompressionLevel.Optimal;
    public int SampleCount => (int)samples;

    public VulkanRenderer(IWindow window)
    {
        this.window = window;
        if (window.VkSurface is null)
            throw new PlatformNotSupportedException("The window has no Vulkan surface.");

        validation = HasValidationLayer();
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateDevice();
        ChooseSurfaceFormat();
        CreateRenderPass();
        CreatePipelines();
        CreateSwapchain();
        CreateCommandsAndSync();

        window.FramebufferResize += _ => swapchainDirty = true;
    }

    public bool Draw(Batch batch, Matrix4x4<float> world, Matrix4x4<float> screen, uint clearColor, string? capturePath = null)
    {
        var size = window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0) return false;
        if (swapchainDirty || extent.Width != (uint)size.X || extent.Height != (uint)size.Y)
            RecreateSwapchain();

        var fence = inFlight[frame];
        vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue);

        uint imageIndex = 0;
        var acquired = khrSwapchain.AcquireNextImage(device, swapchain, ulong.MaxValue, imageAvailable[frame], default, ref imageIndex);
        if (acquired == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }
        if (acquired != Result.Success && acquired != Result.SuboptimalKhr)
            Check(acquired, "vkAcquireNextImageKHR");

        vk.ResetFences(device, 1, &fence);
        Upload(batch);

        HostBuffer capture = default;
        bool capturing = capturePath is not null && canCapture;
        if (capturing)
            capture = CreateHostBuffer((ulong)extent.Width * extent.Height * 4, BufferUsageFlags.TransferDstBit);

        var cb = commandBuffers[frame];
        Record(cb, imageIndex, batch, world, screen, clearColor, capturing ? capture.Buffer : default);

        var waitSemaphore = imageAvailable[frame];
        var signalSemaphore = renderFinished[imageIndex];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };
        Check(vk.QueueSubmit(graphicsQueue, 1, &submit, fence), "vkQueueSubmit");

        if (capturing)
        {
            vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue);
            SaveCapture(capture, capturePath!);
            DestroyHostBuffer(ref capture);
        }

        var chain = swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &chain,
            PImageIndices = &imageIndex,
        };
        var presented = khrSwapchain.QueuePresent(presentQueue, &present);
        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
            swapchainDirty = true;
        else
            Check(presented, "vkQueuePresentKHR");

        frame = (frame + 1) % FramesInFlight;
        return capturing;
    }

    void Upload(Batch batch)
    {
        var vertices = batch.Vertices;
        var indices = batch.Indices;
        EnsureCapacity(ref vertexBuffers[frame], (ulong)(vertices.Length * sizeof(Vertex)), BufferUsageFlags.VertexBufferBit);
        EnsureCapacity(ref indexBuffers[frame], (ulong)(indices.Length * sizeof(uint)), BufferUsageFlags.IndexBufferBit);
        vertices.CopyTo(new Span<Vertex>(vertexBuffers[frame].Mapped, vertices.Length));
        indices.CopyTo(new Span<uint>(indexBuffers[frame].Mapped, indices.Length));
    }

    void EnsureCapacity(ref HostBuffer buffer, ulong size, BufferUsageFlags usage)
    {
        if (buffer.Size >= size && buffer.Buffer.Handle != 0) return;
        DestroyHostBuffer(ref buffer);
        buffer = CreateHostBuffer(Math.Max(System.Numerics.BitOperations.RoundUpToPowerOf2(size), 1UL << 16), usage);
    }

    void Record(CommandBuffer cb, uint imageIndex, Batch batch, Matrix4x4<float> world, Matrix4x4<float> screen,
        uint clearColor, VkBuffer captureBuffer)
    {
        Check(vk.ResetCommandBuffer(cb, 0), "vkResetCommandBuffer");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(vk.BeginCommandBuffer(cb, &begin), "vkBeginCommandBuffer");

        var (r, g, b) = Rgba.Unpack(clearColor);
        var clear = new ClearValue { Color = new ClearColorValue(r, g, b, 1f) };
        var pass = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffers[imageIndex],
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        vk.CmdBeginRenderPass(cb, &pass, SubpassContents.Inline);

        var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
        vk.CmdSetViewport(cb, 0, 1, &viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        vk.CmdSetScissor(cb, 0, 1, &scissor);

        if (batch.Indices.Length > 0)
        {
            var vertexBuffer = vertexBuffers[frame].Buffer;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(cb, 0, 1, &vertexBuffer, &offset);
            vk.CmdBindIndexBuffer(cb, indexBuffers[frame].Buffer, 0, IndexType.Uint32);

            float* matrix = stackalloc float[16];
            Pipeline bound = default;
            Space? pushed = null;
            foreach (var call in batch.Calls)
            {
                var pipeline = call.Blend == Blend.Additive ? additivePipeline : alphaPipeline;
                if (pipeline.Handle != bound.Handle)
                {
                    vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                    bound = pipeline;
                }
                if (pushed != call.Space)
                {
                    ColumnMajor(call.Space == Space.World ? world : screen, matrix);
                    vk.CmdPushConstants(cb, pipelineLayout, ShaderStageFlags.VertexBit, 0, 64, matrix);
                    pushed = call.Space;
                }
                vk.CmdDrawIndexed(cb, call.IndexCount, 1, call.FirstIndex, 0, 0);
            }
        }

        vk.CmdEndRenderPass(cb);

        if (captureBuffer.Handle != 0)
            RecordCapture(cb, swapImages[imageIndex], captureBuffer);

        Check(vk.EndCommandBuffer(cb), "vkEndCommandBuffer");
    }

    static void ColumnMajor(Matrix4x4<float> m, float* destination)
    {
        destination[0] = m.M11; destination[1] = m.M21; destination[2] = m.M31; destination[3] = m.M41;
        destination[4] = m.M12; destination[5] = m.M22; destination[6] = m.M32; destination[7] = m.M42;
        destination[8] = m.M13; destination[9] = m.M23; destination[10] = m.M33; destination[11] = m.M43;
        destination[12] = m.M14; destination[13] = m.M24; destination[14] = m.M34; destination[15] = m.M44;
    }

    void RecordCapture(CommandBuffer cb, VkImage image, VkBuffer buffer)
    {
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.PresentSrcKhr,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransfer);

        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
        };
        vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, buffer, 1, &region);

        var toPresent = toTransfer with
        {
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = 0,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
        };
        var toHost = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Size = Vk.WholeSize,
        };
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit | PipelineStageFlags.BottomOfPipeBit,
            0, 0, null, 1, &toHost, 1, &toPresent);
    }

    void SaveCapture(HostBuffer buffer, string path)
    {
        int width = (int)extent.Width, height = (int)extent.Height;
        var pixels = new byte[width * height * 4];
        new ReadOnlySpan<byte>(buffer.Mapped, pixels.Length).CopyTo(pixels);
        bool bgra = surfaceFormat.Format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (bgra) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }
        PngWriter.Save(path, width, height, pixels, CaptureCompression);
    }

    bool HasValidationLayer()
    {
        if (Environment.GetEnvironmentVariable("VF_VALIDATION") == "0") return false;
        uint count = 0;
        vk.EnumerateInstanceLayerProperties(ref count, null);
        var layers = new LayerProperties[count];
        fixed (LayerProperties* p = layers)
            vk.EnumerateInstanceLayerProperties(ref count, p);
        foreach (var layer in layers)
            if (SilkMarshal.PtrToString((nint)layer.LayerName) == ValidationLayer)
                return true;
        return false;
    }

    void CreateInstance()
    {
        var appName = (byte*)SilkMarshal.StringToPtr("Vector Field");
        var engineName = (byte*)SilkMarshal.StringToPtr("Epsilon");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = engineName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version11,
        };

        var required = window.VkSurface!.GetRequiredExtensions(out uint requiredCount);
        var extensions = SilkMarshal.PtrToStringArray((nint)required, (int)requiredCount).ToList();
        if (validation) extensions.Add(ExtDebugUtils.ExtensionName);
        string[] layers = validation ? [ValidationLayer] : [];

        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        var layerNames = (byte**)SilkMarshal.StringArrayToPtr(layers);
        var info = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = extensionNames,
            EnabledLayerCount = (uint)layers.Length,
            PpEnabledLayerNames = layerNames,
        };
        try
        {
            Check(vk.CreateInstance(&info, null, out instance), "vkCreateInstance");
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free((nint)extensionNames);
            SilkMarshal.Free((nint)layerNames);
        }

        if (!vk.TryGetInstanceExtension(instance, out khrSurface))
            throw new NotSupportedException("VK_KHR_surface is not available.");

        if (validation && vk.TryGetInstanceExtension(instance, out ExtDebugUtils utils))
        {
            debugUtils = utils;
            debugCallback = new PfnDebugUtilsMessengerCallbackEXT(OnDebugMessage);
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                              DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = debugCallback,
            };
            Check(utils.CreateDebugUtilsMessenger(instance, &messengerInfo, null, out debugMessenger), "vkCreateDebugUtilsMessengerEXT");
        }
    }

    static uint OnDebugMessage(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        Console.Error.WriteLine($"[vulkan {severity}] {SilkMarshal.PtrToString((nint)data->PMessage)}");
        return Vk.False;
    }

    void CreateSurface() =>
        surface = window.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();

    void PickPhysicalDevice()
    {
        var candidates = vk.GetPhysicalDevices(instance)
            .Select(d => { vk.GetPhysicalDeviceProperties(d, out var p); return (Device: d, Properties: p); })
            .OrderBy(c => c.Properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 0,
                PhysicalDeviceType.IntegratedGpu => 1,
                _ => 2,
            });

        foreach (var (candidate, properties) in candidates)
        {
            if (!SupportsSwapchain(candidate) || !FindQueueFamilies(candidate, out uint graphics, out uint present))
                continue;
            physicalDevice = candidate;
            graphicsFamily = graphics;
            presentFamily = present;
            DeviceName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "GPU";

            var counts = properties.Limits.FramebufferColorSampleCounts;
            samples = counts.HasFlag(SampleCountFlags.Count4Bit) ? SampleCountFlags.Count4Bit
                : counts.HasFlag(SampleCountFlags.Count2Bit) ? SampleCountFlags.Count2Bit
                : SampleCountFlags.Count1Bit;
            return;
        }
        throw new NotSupportedException("No GPU can draw to this window with Vulkan.");
    }

    bool SupportsSwapchain(PhysicalDevice candidate)
    {
        uint count = 0;
        vk.EnumerateDeviceExtensionProperties(candidate, (byte*)null, ref count, null);
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = extensions)
            vk.EnumerateDeviceExtensionProperties(candidate, (byte*)null, ref count, p);
        return extensions.Any(e => SilkMarshal.PtrToString((nint)e.ExtensionName) == KhrSwapchain.ExtensionName);
    }

    bool FindQueueFamilies(PhysicalDevice candidate, out uint graphics, out uint present)
    {
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref count, p);

        graphics = present = uint.MaxValue;
        for (uint i = 0; i < count; i++)
        {
            bool hasGraphics = families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit);
            khrSurface.GetPhysicalDeviceSurfaceSupport(candidate, i, surface, out var supported);
            if (hasGraphics && supported)
            {
                graphics = present = i;
                return true;
            }
            if (hasGraphics && graphics == uint.MaxValue) graphics = i;
            if (supported && present == uint.MaxValue) present = i;
        }
        return graphics != uint.MaxValue && present != uint.MaxValue;
    }

    void CreateDevice()
    {
        uint[] families = graphicsFamily == presentFamily ? [graphicsFamily] : [graphicsFamily, presentFamily];
        float priority = 1f;
        var queues = stackalloc DeviceQueueCreateInfo[families.Length];
        for (int i = 0; i < families.Length; i++)
        {
            queues[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = families[i],
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        var features = new PhysicalDeviceFeatures();
        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr([KhrSwapchain.ExtensionName]);
        var info = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)families.Length,
            PQueueCreateInfos = queues,
            PEnabledFeatures = &features,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = extensionNames,
        };
        try
        {
            Check(vk.CreateDevice(physicalDevice, &info, null, out device), "vkCreateDevice");
        }
        finally
        {
            SilkMarshal.Free((nint)extensionNames);
        }

        vk.GetDeviceQueue(device, graphicsFamily, 0, out graphicsQueue);
        vk.GetDeviceQueue(device, presentFamily, 0, out presentQueue);
        if (!vk.TryGetDeviceExtension(instance, device, out khrSwapchain))
            throw new NotSupportedException("VK_KHR_swapchain is not available.");
    }

    void ChooseSurfaceFormat()
    {
        uint count = 0;
        khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref count, null);
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
            khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref count, p);

        surfaceFormat = formats.FirstOrDefault(f => f.Format == Format.B8G8R8A8Unorm && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr);
        if (surfaceFormat.Format == Format.Undefined)
            surfaceFormat = formats.FirstOrDefault(f => f.Format == Format.R8G8B8A8Unorm && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr);
        if (surfaceFormat.Format == Format.Undefined)
            surfaceFormat = formats[0];
    }

    void CreateSwapchain()
    {
        khrSurface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out var caps);
        var size = window.FramebufferSize;
        extent = caps.CurrentExtent.Width != uint.MaxValue
            ? caps.CurrentExtent
            : new Extent2D(
                Math.Clamp((uint)Math.Max(size.X, 1), caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Math.Clamp((uint)Math.Max(size.Y, 1), caps.MinImageExtent.Height, caps.MaxImageExtent.Height));

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);

        canCapture = caps.SupportedUsageFlags.HasFlag(ImageUsageFlags.TransferSrcBit);
        var usage = ImageUsageFlags.ColorAttachmentBit | (canCapture ? ImageUsageFlags.TransferSrcBit : 0);

        var compositeAlpha = caps.SupportedCompositeAlpha.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr)
            ? CompositeAlphaFlagsKHR.OpaqueBitKhr
            : (CompositeAlphaFlagsKHR)((uint)caps.SupportedCompositeAlpha & (uint)-(int)caps.SupportedCompositeAlpha);

        uint* families = stackalloc uint[] { graphicsFamily, presentFamily };
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = graphicsFamily == presentFamily ? SharingMode.Exclusive : SharingMode.Concurrent,
            QueueFamilyIndexCount = graphicsFamily == presentFamily ? 0u : 2u,
            PQueueFamilyIndices = graphicsFamily == presentFamily ? null : families,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = compositeAlpha,
            PresentMode = ChoosePresentMode(),
            Clipped = true,
        };
        Check(khrSwapchain.CreateSwapchain(device, &info, null, out swapchain), "vkCreateSwapchainKHR");

        uint count = 0;
        khrSwapchain.GetSwapchainImages(device, swapchain, ref count, null);
        swapImages = new VkImage[count];
        fixed (VkImage* p = swapImages)
            khrSwapchain.GetSwapchainImages(device, swapchain, ref count, p);

        swapViews = swapImages.Select(image => CreateImageView(image, surfaceFormat.Format)).ToArray();

        if (samples != SampleCountFlags.Count1Bit)
            CreateMsaaTarget();

        framebuffers = new Framebuffer[count];
        renderFinished = new VkSemaphore[count];
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var attachments = stackalloc ImageView[2];
        for (int i = 0; i < count; i++)
        {
            uint attachmentCount;
            if (samples != SampleCountFlags.Count1Bit)
            {
                attachments[0] = msaaView;
                attachments[1] = swapViews[i];
                attachmentCount = 2;
            }
            else
            {
                attachments[0] = swapViews[i];
                attachmentCount = 1;
            }
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = attachmentCount,
                PAttachments = attachments,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(device, &framebufferInfo, null, out framebuffers[i]), "vkCreateFramebuffer");
            Check(vk.CreateSemaphore(device, &semaphoreInfo, null, out renderFinished[i]), "vkCreateSemaphore");
        }
        swapchainDirty = false;
    }

    PresentModeKHR ChoosePresentMode()
    {
        if (Environment.GetEnvironmentVariable("VF_VSYNC") != "0") return PresentModeKHR.FifoKhr;
        uint count = 0;
        khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref count, null);
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* p = modes)
            khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref count, p);
        return modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr
            : modes.Contains(PresentModeKHR.ImmediateKhr) ? PresentModeKHR.ImmediateKhr
            : PresentModeKHR.FifoKhr;
    }

    void CreateMsaaTarget()
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = surfaceFormat.Format,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(vk.CreateImage(device, &info, null, out msaaImage), "vkCreateImage");
        vk.GetImageMemoryRequirements(device, msaaImage, out var requirements);
        uint type = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.LazilyAllocatedBit, out bool found);
        if (!found) type = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = type,
        };
        Check(vk.AllocateMemory(device, &allocation, null, out msaaMemory), "vkAllocateMemory");
        Check(vk.BindImageMemory(device, msaaImage, msaaMemory, 0), "vkBindImageMemory");
        msaaView = CreateImageView(msaaImage, surfaceFormat.Format);
    }

    ImageView CreateImageView(VkImage image, Format format)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Check(vk.CreateImageView(device, &info, null, out var view), "vkCreateImageView");
        return view;
    }

    void RecreateSwapchain()
    {
        var size = window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0) return;
        vk.DeviceWaitIdle(device);
        DestroySwapchain();
        CreateSwapchain();
    }

    void DestroySwapchain()
    {
        foreach (var fb in framebuffers) vk.DestroyFramebuffer(device, fb, null);
        foreach (var semaphore in renderFinished) vk.DestroySemaphore(device, semaphore, null);
        foreach (var view in swapViews) vk.DestroyImageView(device, view, null);
        if (msaaView.Handle != 0)
        {
            vk.DestroyImageView(device, msaaView, null);
            vk.DestroyImage(device, msaaImage, null);
            vk.FreeMemory(device, msaaMemory, null);
            msaaView = default;
            msaaImage = default;
            msaaMemory = default;
        }
        khrSwapchain.DestroySwapchain(device, swapchain, null);
        framebuffers = [];
        renderFinished = [];
        swapViews = [];
        swapImages = [];
    }

    void CreateRenderPass()
    {
        bool msaa = samples != SampleCountFlags.Count1Bit;
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = surfaceFormat.Format,
            Samples = samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = msaa ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = msaa ? ImageLayout.ColorAttachmentOptimal : ImageLayout.PresentSrcKhr,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = surfaceFormat.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        var colorReference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var resolveReference = new AttachmentReference(1, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorReference,
            PResolveAttachments = msaa ? &resolveReference : null,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = msaa ? 2u : 1u,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        Check(vk.CreateRenderPass(device, &info, null, out renderPass), "vkCreateRenderPass");
    }

    void CreatePipelines()
    {
        var pushConstants = new PushConstantRange(ShaderStageFlags.VertexBit, 0, 64);
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };
        Check(vk.CreatePipelineLayout(device, &layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

        var vertexModule = CreateShaderModule("shape.vert.spv");
        var fragmentModule = CreateShaderModule("shape.frag.spv");
        try
        {
            alphaPipeline = CreatePipeline(vertexModule, fragmentModule, BlendFactor.OneMinusSrcAlpha);
            additivePipeline = CreatePipeline(vertexModule, fragmentModule, BlendFactor.One);
        }
        finally
        {
            vk.DestroyShaderModule(device, vertexModule, null);
            vk.DestroyShaderModule(device, fragmentModule, null);
        }
    }

    Pipeline CreatePipeline(ShaderModule vertexModule, ShaderModule fragmentModule, BlendFactor destination)
    {
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentModule,
                PName = entry,
            };

            var binding = new VertexInputBindingDescription(0, (uint)sizeof(Vertex), VertexInputRate.Vertex);
            var attributes = stackalloc VertexInputAttributeDescription[2];
            attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
            attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R8G8B8A8Unorm, 8);
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = samples,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = destination,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = pipelineLayout,
                RenderPass = renderPass,
                Subpass = 0,
            };
            Check(vk.CreateGraphicsPipelines(device, default, 1, &info, null, out var pipeline), "vkCreateGraphicsPipelines");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
        }
    }

    ShaderModule CreateShaderModule(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"The shader {resource} is not embedded. Is glslc installed?");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            Check(vk.CreateShaderModule(device, &info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    void CreateCommandsAndSync()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = graphicsFamily,
        };
        Check(vk.CreateCommandPool(device, &poolInfo, null, out commandPool), "vkCreateCommandPool");

        var allocation = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = FramesInFlight,
        };
        fixed (CommandBuffer* p = commandBuffers)
            Check(vk.AllocateCommandBuffers(device, &allocation, p), "vkAllocateCommandBuffers");

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        for (int i = 0; i < FramesInFlight; i++)
        {
            Check(vk.CreateSemaphore(device, &semaphoreInfo, null, out imageAvailable[i]), "vkCreateSemaphore");
            Check(vk.CreateFence(device, &fenceInfo, null, out inFlight[i]), "vkCreateFence");
        }
    }

    HostBuffer CreateHostBuffer(ulong size, BufferUsageFlags usage)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        var result = new HostBuffer { Size = size };
        Check(vk.CreateBuffer(device, &info, null, out result.Buffer), "vkCreateBuffer");
        vk.GetBufferMemoryRequirements(device, result.Buffer, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        Check(vk.AllocateMemory(device, &allocation, null, out result.Memory), "vkAllocateMemory");
        Check(vk.BindBufferMemory(device, result.Buffer, result.Memory, 0), "vkBindBufferMemory");
        void* mapped;
        Check(vk.MapMemory(device, result.Memory, 0, size, 0, &mapped), "vkMapMemory");
        result.Mapped = mapped;
        return result;
    }

    void DestroyHostBuffer(ref HostBuffer buffer)
    {
        if (buffer.Buffer.Handle == 0) return;
        vk.UnmapMemory(device, buffer.Memory);
        vk.DestroyBuffer(device, buffer.Buffer, null);
        vk.FreeMemory(device, buffer.Memory, null);
        buffer = default;
    }

    uint FindMemoryType(uint typeBits, MemoryPropertyFlags flags)
    {
        uint type = FindMemoryType(typeBits, flags, out bool found);
        return found ? type : throw new NotSupportedException($"No memory type with {flags}.");
    }

    uint FindMemoryType(uint typeBits, MemoryPropertyFlags flags, out bool found)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var properties);
        for (int i = 0; i < properties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (properties.MemoryTypes[i].PropertyFlags & flags) == flags)
            {
                found = true;
                return (uint)i;
            }
        }
        found = false;
        return 0;
    }

    static void Check(Result result, string call)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{call} failed: {result}.");
    }

    public void Dispose()
    {
        if (device.Handle == 0) return;
        vk.DeviceWaitIdle(device);

        DestroySwapchain();
        for (int i = 0; i < FramesInFlight; i++)
        {
            DestroyHostBuffer(ref vertexBuffers[i]);
            DestroyHostBuffer(ref indexBuffers[i]);
            vk.DestroySemaphore(device, imageAvailable[i], null);
            vk.DestroyFence(device, inFlight[i], null);
        }
        vk.DestroyCommandPool(device, commandPool, null);
        vk.DestroyPipeline(device, alphaPipeline, null);
        vk.DestroyPipeline(device, additivePipeline, null);
        vk.DestroyPipelineLayout(device, pipelineLayout, null);
        vk.DestroyRenderPass(device, renderPass, null);
        vk.DestroyDevice(device, null);
        device = default;

        khrSurface.DestroySurface(instance, surface, null);
        if (debugUtils is not null)
            debugUtils.DestroyDebugUtilsMessenger(instance, debugMessenger, null);
        vk.DestroyInstance(instance, null);
        vk.Dispose();
    }
}
