﻿using Vulkan;
using static Vulkan.VulkanNative;
using static Veldrid.Vk.VulkanUtil;
using System;
using System.Diagnostics;

namespace Veldrid.Vk
{
    internal unsafe class VkTexture : Texture, VkDeferredDisposal
    {
        private readonly VkGraphicsDevice _gd;
        private readonly VkImage _optimalImage;
        private readonly VkMemoryBlock _optimalMemory;
        private readonly VkImage[] _stagingImages;
        private readonly VkMemoryBlock[] _stagingMemories;
        private readonly uint _actualImageArrayLayers;
        private bool _destroyed;

        public override uint Width { get; }

        public override uint Height { get; }

        public override uint Depth { get; }

        public override PixelFormat Format { get; }

        public override uint MipLevels { get; }

        public override uint ArrayLayers { get; }

        public override TextureUsage Usage { get; }

        public override TextureSampleCount SampleCount { get; }

        public VkImage OptimalDeviceImage => _optimalImage;
        public VkMemoryBlock OptimalMemoryBlock => _optimalMemory;

        public VkImage GetStagingImage(uint subresource) => _stagingImages[subresource];
        public VkMemoryBlock GetStagingMemoryBlock(uint subresource) => _stagingMemories[subresource];

        public VkFormat VkFormat { get; }
        public VkSampleCountFlags VkSampleCount { get; }

        public ReferenceTracker ReferenceTracker { get; } = new ReferenceTracker();

        private VkImageLayout[] _imageLayouts;
        private string _name;

        internal VkTexture(VkGraphicsDevice gd, ref TextureDescription description)
        {
            _gd = gd;
            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            bool isCubemap = ((description.Usage) & TextureUsage.Cubemap) == TextureUsage.Cubemap;
            _actualImageArrayLayers = isCubemap
                ? 6 * ArrayLayers
                : ArrayLayers;
            Format = description.Format;
            Usage = description.Usage;
            SampleCount = description.SampleCount;
            VkSampleCount = VkFormats.VdToVkSampleCount(SampleCount);
            VkFormat = VkFormats.VdToVkPixelFormat(Format, (description.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil);

            VkImageCreateInfo imageCI = VkImageCreateInfo.New();
            imageCI.mipLevels = MipLevels;
            imageCI.arrayLayers = _actualImageArrayLayers;
            imageCI.imageType = Depth == 1 ? VkImageType.Image2D : VkImageType.Image3D;
            imageCI.extent.width = Width;
            imageCI.extent.height = Height;
            imageCI.extent.depth = Depth;
            imageCI.initialLayout = VkImageLayout.Preinitialized;
            imageCI.usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc;
            bool isDepthStencil = (description.Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil;
            if ((description.Usage & TextureUsage.Sampled) == TextureUsage.Sampled)
            {
                imageCI.usage |= VkImageUsageFlags.Sampled;
            }
            if (isDepthStencil)
            {
                imageCI.usage |= VkImageUsageFlags.DepthStencilAttachment;
            }
            if ((description.Usage & TextureUsage.RenderTarget) == TextureUsage.RenderTarget)
            {
                imageCI.usage |= VkImageUsageFlags.ColorAttachment;
            }
            if ((description.Usage & TextureUsage.Storage) == TextureUsage.Storage)
            {
                imageCI.usage |= VkImageUsageFlags.Storage;
            }

            bool isStaging = (Usage & TextureUsage.Staging) == TextureUsage.Staging;

            imageCI.tiling = isStaging ? VkImageTiling.Linear : VkImageTiling.Optimal;
            imageCI.format = VkFormat;

            imageCI.samples = VkSampleCount;
            if (isCubemap)
            {
                imageCI.flags = VkImageCreateFlags.CubeCompatible;
            }

            uint subresourceCount = MipLevels * _actualImageArrayLayers;
            if (!isStaging)
            {
                VkResult result = vkCreateImage(gd.Device, ref imageCI, null, out _optimalImage);
                CheckResult(result);

                vkGetImageMemoryRequirements(gd.Device, _optimalImage, out VkMemoryRequirements memoryRequirements);

                VkMemoryBlock memoryToken = gd.MemoryManager.Allocate(
                    gd.PhysicalDeviceMemProperties,
                    memoryRequirements.memoryTypeBits,
                    VkMemoryPropertyFlags.DeviceLocal,
                    false,
                    memoryRequirements.size,
                    memoryRequirements.alignment);
                _optimalMemory = memoryToken;
                vkBindImageMemory(gd.Device, _optimalImage, _optimalMemory.DeviceMemory, _optimalMemory.Offset);
            }
            else
            {
                // Linear images must have one array layer and mip level.
                imageCI.arrayLayers = 1;
                imageCI.mipLevels = 1;

                _stagingImages = new VkImage[subresourceCount];
                _stagingMemories = new VkMemoryBlock[subresourceCount];
                for (uint arrayLayer = 0; arrayLayer < ArrayLayers; arrayLayer++)
                {
                    for (uint level = 0; level < MipLevels; level++)
                    {
                        uint subresource = CalculateSubresource(level, arrayLayer);
                        Util.GetMipDimensions(
                            this,
                            level,
                            out imageCI.extent.width,
                            out imageCI.extent.height,
                            out imageCI.extent.depth);

                        VkResult result = vkCreateImage(gd.Device, ref imageCI, null, out _stagingImages[subresource]);
                        CheckResult(result);

                        vkGetImageMemoryRequirements(
                            gd.Device,
                            _stagingImages[subresource],
                            out VkMemoryRequirements memoryRequirements);

                        VkMemoryBlock memoryToken = gd.MemoryManager.Allocate(
                            gd.PhysicalDeviceMemProperties,
                            memoryRequirements.memoryTypeBits,
                            VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent,
                            true,
                            memoryRequirements.size,
                            memoryRequirements.alignment);
                        _stagingMemories[subresource] = memoryToken;

                        result = vkBindImageMemory(
                            gd.Device,
                            _stagingImages[subresource],
                            memoryToken.DeviceMemory,
                            memoryToken.Offset);
                        CheckResult(result);
                    }
                }
            }

            _imageLayouts = new VkImageLayout[subresourceCount];
            for (int i = 0; i < _imageLayouts.Length; i++)
            {
                _imageLayouts[i] = VkImageLayout.Preinitialized;
            }
        }

        internal VkTexture(
            VkGraphicsDevice gd,
            uint width,
            uint height,
            uint mipLevels,
            uint arrayLayers,
            VkFormat vkFormat,
            TextureUsage usage,
            TextureSampleCount sampleCount,
            VkImage existingImage)
        {
            Debug.Assert(width > 0 && height > 0);
            _gd = gd;
            MipLevels = mipLevels;
            Width = width;
            Height = height;
            Depth = 1;
            VkFormat = vkFormat;
            Format = VkFormats.VkToVdPixelFormat(VkFormat);
            ArrayLayers = arrayLayers;
            Usage = usage;
            SampleCount = sampleCount;
            VkSampleCount = VkFormats.VdToVkSampleCount(sampleCount);
            _optimalImage = existingImage;
        }

        internal VkSubresourceLayout GetSubresourceLayout(uint subresource)
        {
            bool staging = _stagingImages != null;
            Util.GetMipLevelAndArrayLayer(this, subresource, out uint mipLevel, out uint arrayLayer);
            VkImageAspectFlags aspect = (Usage & TextureUsage.DepthStencil) == TextureUsage.DepthStencil
                ? (VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil)
                : VkImageAspectFlags.Color;
            VkImageSubresource imageSubresource = new VkImageSubresource
            {
                arrayLayer = staging ? 0 : arrayLayer,
                mipLevel = staging ? 0 : mipLevel,
                aspectMask = aspect,
            };

            VkImage image = staging
                ? _stagingImages[subresource]
                : _optimalImage;

            vkGetImageSubresourceLayout(_gd.Device, image, ref imageSubresource, out VkSubresourceLayout layout);
            return layout;
        }

        internal void TransitionImageLayout(
            VkCommandBuffer cb,
            uint baseMipLevel,
            uint levelCount,
            uint baseArrayLayer,
            uint layerCount,
            VkImageLayout newLayout)
        {
            VkImageLayout oldLayout = _imageLayouts[CalculateSubresource(baseMipLevel, baseArrayLayer)];
#if DEBUG
            for (uint level = 0; level < levelCount; level++)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    if (_imageLayouts[CalculateSubresource(baseMipLevel + level, baseArrayLayer + layer)] != oldLayout)
                    {
                        throw new VeldridException("Unexpected image layout.");
                    }
                }
            }
#endif
            if (oldLayout != newLayout)
            {
                if (_stagingImages == null)
                {
                    VulkanUtil.TransitionImageLayout(
                        cb,
                        OptimalDeviceImage,
                        baseMipLevel,
                        levelCount,
                        baseArrayLayer,
                        layerCount,
                        _imageLayouts[CalculateSubresource(baseMipLevel, baseArrayLayer)],
                        newLayout);

                    for (uint level = 0; level < levelCount; level++)
                    {
                        for (uint layer = 0; layer < layerCount; layer++)
                        {
                            _imageLayouts[CalculateSubresource(baseMipLevel + level, baseArrayLayer + layer)] = newLayout;
                        }
                    }
                }
                else
                {
                    // Transition each staging image one-by-one.
                    for (uint arrayLayer = baseArrayLayer; arrayLayer < baseArrayLayer + layerCount; arrayLayer++)
                    {
                        for (uint level = baseMipLevel; level < baseMipLevel + levelCount; level++)
                        {
                            uint subresource = CalculateSubresource(level, arrayLayer);
                            VkImage image = _stagingImages[subresource];
                            VulkanUtil.TransitionImageLayout(
                                cb,
                                image,
                                0, 1,
                                0, 1,
                                _imageLayouts[subresource],
                                newLayout);
                            _imageLayouts[subresource] = newLayout;
                        }
                    }
                }
            }
        }

        internal VkImageLayout GetImageLayout(uint mipLevel, uint arrayLayer)
        {
            return _imageLayouts[CalculateSubresource(mipLevel, arrayLayer)];
        }

        internal VkMemoryBlock GetMemoryBlock(uint subresource)
        {
            if (_stagingMemories != null)
            {
                return _stagingMemories[subresource];
            }
            else
            {
                return _optimalMemory;
            }
        }

        public override string Name
        {
            get => _name;
            set
            {
                _name = value;
                _gd.SetResourceName(this, value);
            }
        }

        public override void Dispose()
        {
            _gd.DeferredDisposal(this);
        }

        public void DestroyResources()
        {
            if (!_destroyed)
            {
                _destroyed = true;

                bool isStaging = (Usage & TextureUsage.Staging) == TextureUsage.Staging;
                if (isStaging)
                {
                    for (int i = 0; i < _stagingImages.Length; i++)
                    {
                        vkDestroyImage(_gd.Device, _stagingImages[i], null);
                        _gd.MemoryManager.Free(_stagingMemories[i]);
                    }
                }
                else
                {
                    vkDestroyImage(_gd.Device, _optimalImage, null);
                    if (_optimalMemory != null)
                    {
                        _gd.MemoryManager.Free(_optimalMemory);
                    }
                }
            }
        }
    }
}
