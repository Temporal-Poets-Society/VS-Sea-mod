using System;
using System.Linq;
using Sea;

namespace SeaTests
{
    static class FoamChecks
    {
        public static void Run(Action<bool,string> check)
        {
            Console.WriteLine("issue 35: actual wind input, compass axes, foam freshness");
            var model=new WindModel(new WorldShape(),new SeaConfig {SeaWindStrength=0.3f,SeaWindFrom=270});
            var fallback=model.SeaWind(50000,50000,3000,true);
            model.Provider=new WindProvider("earth-test",(x,z,h,g)=> (0.0,1.2),null);
            var actual=model.SeaWind(50000,50000,3000,true);
            check(Math.Abs(fallback.Strength-0.3)<1e-6 && actual.X==0 && actual.Z==1.2
                && WaveSpectrum.WhitecapCoverage(actual.Strength)>300*WaveSpectrum.WhitecapCoverage(fallback.Strength),
                "same SeaWind call as foam uploader: Earth provider 1.2 toward south wins over steady config 0.3 from west");
            var east=Wind.FromCompass(270,1);var south=Wind.FromCompass(0,1);var west=Wind.FromCompass(90,1);
            check(Math.Abs(east.X-1)<1e-12 && Math.Abs(east.Z)<1e-12 && Math.Abs(south.X)<1e-12 && south.Z==1 && Math.Abs(west.X+1)<1e-12,
                "wind-from 270/0/90 maps to travel +X/+Z/-X; north is -Z, east is +X");
        }
    }
}
