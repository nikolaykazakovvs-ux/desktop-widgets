using System;
using LibreHardwareMonitor.Hardware;

namespace ResourcesWidgetSensor
{
    // Direct read-only library use. No GUI assembly, HTTP server or hardware-control operations.
    sealed class SensorReader : IDisposable
    {
        readonly Computer computer = new Computer { IsCpuEnabled = true };
        public SensorReader() { computer.Open(); }
        public double? Read(out string label)
        {
            label = null;
            double? chosen = null;
            int best = int.MaxValue;
            foreach (var hardware in computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu) continue;
                hardware.Update();
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue) continue;
                    double value = sensor.Value.Value;
                    if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value >= 150) continue;
                    string name = sensor.Name ?? "CPU";
                    int rank = Rank(name);
                    if (rank > best || (rank == best && chosen.HasValue && value <= chosen.Value)) continue;
                    best = rank;
                    chosen = value;
                    label = name.Length > 96 ? name.Substring(0, 96) : name;
                }
            }
            return chosen;
        }
        static int Rank(string name)
        {
            if (name.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (name.IndexOf("Diode", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            return 10;
        }
        public void Dispose() { computer.Close(); }
    }
}
