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
            check(FoamMath.StreakRamp(0.3)==0 && FoamMath.StreakRamp(0.68)==0 && FoamMath.StreakRamp(0.8)>0 && FoamMath.StreakRamp(1.2)==1,
                "windrow ramp starts at 17m/s (about Beaufort 8), independently of the whitecap onset");
            var east=Wind.FromCompass(270,1);var south=Wind.FromCompass(0,1);var west=Wind.FromCompass(90,1);
            check(Math.Abs(east.X-1)<1e-12 && Math.Abs(east.Z)<1e-12 && Math.Abs(south.X)<1e-12 && south.Z==1 && Math.Abs(west.X+1)<1e-12,
                "wind-from 270/0/90 maps to travel +X/+Z/-X; north is -Z, east is +X");
            var field=new WakeField();var map=new WakeMap();var hull=new WakeHull(26,7,2.5);
            field.Observe(1,0,0,0,0,4,0,hull);field.Observe(1,0.5,2,0,0,4,0,hull);map.Paint(field,0.5,0,0,0);
            bool valid=true,fresh=false;
            for(int i=0;i<map.Pixels.Length;i+=4)if(map.Pixels[i+3]>0.001){
                valid &= map.Pixels[i+1]>=0 && map.Pixels[i+1]<=map.Pixels[i+3];fresh |= map.Pixels[i+1]/map.Pixels[i+3]>0.99;
            }
            check(valid && fresh,"fresh bow/wash stores bounded premultiplied freshness in the existing spare atlas channel");
            map.Paint(field,60,0,0,0);double expected=1-59.5/WakeMath.FoamLife(26);bool any=false,aged=true;
            for(int i=0;i<map.Pixels.Length;i+=4)if(map.Pixels[i+3]>0.001){
                any=true;aged &= Math.Abs(map.Pixels[i+1]/map.Pixels[i+3]-expected)<1e-6;
            }
            check(any && aged,"60-second stern trail retains real age/freshness independently of wind and foam opacity");
            check(WakeMap.MaxSamples==250000,"coordinator's accepted 250,000-sample raster cap remains unchanged");
        }
    }
}
