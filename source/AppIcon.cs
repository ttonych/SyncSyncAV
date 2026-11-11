// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
namespace SyncSyncAV {
    public static class AppIcon {
        private static readonly BitmapDecoder Decoder=Load();
        private static BitmapDecoder Load(){
            using(var stream=typeof(AppIcon).Assembly.GetManifestResourceStream("SyncSyncAV.ico"))
                return BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        }
        public static BitmapFrame ForSize(int size){
            return Decoder.Frames.OrderBy(f=>f.PixelWidth<size?10000-f.PixelWidth:f.PixelWidth-size).First();
        }
        public static void Apply(Window window){
            // Retain the ICO decoder, so WPF can select native small/big frames.
            window.Icon=Decoder.Frames[0];
        }
    }
}
