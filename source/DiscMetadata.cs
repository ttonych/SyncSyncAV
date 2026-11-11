// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SyncSyncAV {
    public sealed class DiscMetadata {
        public double Fps;public int FpsNumerator,FpsDenominator;public int Width,Height;public string Codec,Origin,ResolvedFile;
        public decimal RoundedMilliseconds(long frames) {
            if(FpsNumerator<=0||FpsDenominator<=0)throw new InvalidOperationException(Ui.Text("UnknownRate"));
            // Same nearest-integer rule as VirtualDub2 VDFraction::scale64ir:
            // exact half milliseconds round away from zero, without double drift.
            return Decimal.Round(frames*1000m*FpsDenominator/FpsNumerator,0,MidpointRounding.AwayFromZero);
        }
        private static int U16(byte[] d,int o){if(o<0||o+2>d.Length)throw new InvalidDataException();return (d[o]<<8)|d[o+1];}
        private static int U32(byte[] d,int o){if(o<0||o+4>d.Length)throw new InvalidDataException();return checked((int)((uint)d[o]<<24|(uint)d[o+1]<<16|(uint)d[o+2]<<8|d[o+3]));}
        private static byte[] Read(string path){var i=new FileInfo(path);if(!i.Exists||i.Length>8*1024*1024)throw new InvalidDataException();return File.ReadAllBytes(path);}
        public static DiscMetadata TryRead(string path) {
            try {
                var paths=new List<string>();string ext=Path.GetExtension(path).ToLowerInvariant();
                if(ext==".mpls") {
                    byte[] d=Read(path);if(d.Length<20||Encoding.ASCII.GetString(d,0,4)!="MPLS")return null;
                    int pos=U32(d,8),end=checked(pos+4+U32(d,pos));if(end>d.Length)return null;
                    int count=U16(d,pos+6);pos+=10;if(count<1||count>2048)return null;
                    for(int i=0;i<count;i++) {
                        int size=U16(d,pos),next=checked(pos+2+size);if(size<9||next>end)return null;
                        string id=Encoding.ASCII.GetString(d,pos+2,5);if(id.Any(c=>c<'0'||c>'9'))return null;
                        paths.Add(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)),"CLIPINF",id+".clpi"));pos=next;
                    }
                } else if(ext==".m2ts") paths.Add(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)),"CLIPINF",Path.GetFileNameWithoutExtension(path)+".clpi"));
                else return null;
                DiscMetadata found=null;
                foreach(string p in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
                    var m=ReadClip(p);if(m==null)return null;
                    if(found!=null&&(found.Fps!=m.Fps||found.Width!=m.Width||found.Height!=m.Height))return null;
                    found=m;
                }
                if(found!=null)found.Origin="CLPI";return found;
            }catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}catch(ArgumentException){return null;}catch(OverflowException){return null;}catch(IndexOutOfRangeException){return null;}
        }
        private static DiscMetadata ReadClip(string path) {
            byte[] d=Read(path);if(d.Length<20||Encoding.ASCII.GetString(d,0,4)!="HDMV")return null;
            int pos=U32(d,12),end=checked(pos+4+U32(d,pos));if(end>d.Length||pos+6>end)return null;
            int programs=d[pos+5];pos+=6;DiscMetadata result=null;
            int[] widths={0,720,720,720,1920,1280,1920,720,3840};int[] heights={0,480,576,480,1080,720,1080,576,2160};
            double[] rates={0,24000.0/1001,24,25,30000.0/1001,0,50,60000.0/1001};
            int[] rateNum={0,24000,24,25,30000,0,50,60000},rateDen={1,1001,1,1,1001,1,1,1001};
            for(int i=0;i<programs;i++) {
                if(pos+8>end)return null;int streams=d[pos+6];pos+=8;
                for(int j=0;j<streams;j++) {
                    if(pos+3>end)return null;int pid=U16(d,pos),len=d[pos+2];pos+=3;if(pos+len>end||len<1)return null;
                    int coding=d[pos];
                    if(pid==0x1011&&(coding==1||coding==2||coding==0x1b||coding==0x20||coding==0x24||coding==0xea)) {
                        if(len<2)return null;int f=d[pos+1]>>4,r=d[pos+1]&15;
                        if(f>=widths.Length||r>=rates.Length||widths[f]==0||rates[r]==0)return null;
                        var m=new DiscMetadata{Fps=rates[r],FpsNumerator=rateNum[r],FpsDenominator=rateDen[r],Width=widths[f],Height=heights[f],Codec=coding==0x24?"HEVC":coding==0x1b?"AVC":null};
                        if(result!=null&&(result.Fps!=m.Fps||result.Width!=m.Width||result.Height!=m.Height))return null;
                        result=m;
                    }
                    pos+=len;
                }
            }
            return result;
        }
    }
}
