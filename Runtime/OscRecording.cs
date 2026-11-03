using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRCFTMapper
{
    [Serializable]
    public struct OscSample
    {
        public float time;
        public string address; // /avatar/parameters/ を除いた名前
        public float value;
    }

    [CreateAssetMenu(menuName = "VRCFT Mapper/OSC Recording")]
    public class OscRecording : ScriptableObject
    {
        public float length;
        public List<OscSample> samples = new List<OscSample>();
    }
}
