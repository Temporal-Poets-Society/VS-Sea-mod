using System;
using System.Diagnostics;
using Sea;

namespace SeaTests
{
    static class CrestChecks
    {
        public static void Run(Action<bool,string> check)
        {
            Console.WriteLine("issue 37: invertible crest geometry");
            var single=new WavePlace();
            single.SetTrains(0,1,new[]{0.2},new[]{0.0},new[]{1.0},new[]{0.0});
            double peak=Math.PI/0.4, trough=3*Math.PI/0.4, d=0.02;
            double curvaturePeak=(single.HeightAt(peak-d,0)-2*single.HeightAt(peak,0)+single.HeightAt(peak+d,0))/(d*d);
            double curvatureTrough=(single.HeightAt(trough-d,0)-2*single.HeightAt(trough,0)+single.HeightAt(trough+d,0))/(d*d);
            check(-curvaturePeak>3*curvatureTrough && Math.Abs(single.HeightAt(peak,0)-1)<1e-7 && Math.Abs(single.HeightAt(trough,0)+1)<1e-7,
                $"horizontal orbits sharpen crests / flatten troughs without changing extrema (curvature ratio {-curvaturePeak/curvatureTrough:F2})");
            var spec=new WaveSpectrum(4242,254000);
            double worst=0, maxBudget=0;int samples=0;
            var watch=Stopwatch.StartNew();
            foreach(double scale in new[]{1.0,2.2,4.0}) foreach(double strength in new[]{0.0,0.3,0.8,1.5}) foreach(double from in new[]{0.0,270,315}) {
                var model=new WindModel(new WorldShape(),new SeaConfig{SeaWindFrom=(float)from,SeaWindStrength=(float)strength,StormHeightScale=(float)scale});
                var p=new WavePlace();p.Update(spec,300.25,model.SeaAt(50000,50000,3000),Wind.FromCompass(from,strength));
                double sum=0;for(int i=0;i<p.Count;i++)sum+=Math.Abs(p.Amp[i])*Math.Sqrt(p.Kx[i]*p.Kx[i]+p.Kz[i]*p.Kz[i])*WavePlace.ShapeWeight(Math.Sqrt(p.Kx[i]*p.Kx[i]+p.Kz[i]*p.Kz[i]));
                maxBudget=Math.Max(maxBudget,sum*p.Shape);
                foreach(double factor in new[]{1.0,0.2,1.0/90}) for(int n=0;n<80;n++) {
                    double qx=812345.5+n*3.7,qz=-233007.25+n*6.1;
                    var at=p.SurfaceAt(qx,qz,factor);
                    worst=Math.Max(worst,Math.Abs(p.HeightAt(at.X,at.Z)*factor-at.Y));samples++;
                }
            }
            watch.Stop();
            check(maxBudget<=WavePlace.MaxCompression+1e-12,"all directions, calm/storm and height scales 1/2.2/4: horizontal Jacobian eigenvalues >= 0.35 (no loops)");
            check(worst<0.001,$"CPU inverse recovers parametric height, including legacy sheltered scaling: {samples} points, max {worst:E3} blocks ({watch.ElapsedMilliseconds} ms)");
            // Worst-phase alignment of a deliberately severe custom spectrum exercises the contraction cap.
            double[] kx=new double[24],kz=new double[24],a=new double[24],ph=new double[24];
            for(int i=0;i<24;i++){kx[i]=0.05+i*0.003;a[i]=0.5;ph[i]=Math.PI/2;}
            single.SetTrains(0,24,kx,kz,a,ph);worst=0;
            for(int n=0;n<200;n++) {var at=single.SurfaceAt((n-100)*0.025,0);worst=Math.Max(worst,Math.Abs(single.HeightAt(at.X,at.Z)-at.Y));}
            check(worst<0.001,$"all 24 custom crests aligned at the .65 compression cap: inverse error {worst:E3} blocks");
            check(!CelestialShader.FoamDeclarations.Contains("sea_WindRow") && !CelestialShader.FoamDeclarations.Contains("sea_WindStreaks"),
                "downwind layer functions removed from the shared production/GPU foam source");

            // (0.6.0) open-sea crest spray: the CPU crest measure is the drawn surface's own compression, and its gate the shader's
            Console.WriteLine("crest spray: the shader's whitecap crests on the CPU");
            var storm=new WavePlace();
            storm.Update(spec,300.25,new WindModel(new WorldShape(),new SeaConfig{SeaWindFrom=270,SeaWindStrength=1.5f}).SeaAt(50000,50000,3000),Wind.FromCompass(270,1.5));
            double worstJ=0;int whitecaps=0,points=0;
            for(int n=0;n<400;n++) {
                double x=50000+n*1.37,z=50000+n*0.61;
                var crest=storm.CrestAt(x,z);
                var q=storm.ParameterAt(x,z);double h=1e-4;
                var px=storm.SurfaceAt(q.X+h,q.Z,1);var mx=storm.SurfaceAt(q.X-h,q.Z,1);
                var pz=storm.SurfaceAt(q.X,q.Z+h,1);var mz=storm.SurfaceAt(q.X,q.Z-h,1);
                double j00=(px.X-mx.X)/(2*h),j10=(px.Z-mx.Z)/(2*h),j01=(pz.X-mz.X)/(2*h),j11=(pz.Z-mz.Z)/(2*h);
                double least=0.5*(j00+j11-Math.Sqrt((j00-j11)*(j00-j11)+(j01+j10)*(j01+j10)));
                worstJ=Math.Max(worstJ,Math.Abs(Math.Max(0,1-least)-crest.Compression));
                if(WavePlace.Whitecap(crest.Height,crest.Compression)>=0.2)whitecaps++;
                points++;
            }
            check(worstJ<1e-5,$"crest compression = 1 - least eigenvalue of the drawn surface's Jacobian: {points} storm points, max error {worstJ:E2}");
            check(whitecaps>0&&whitecaps<points/2,$"force 12: {whitecaps} of {points} places are whitecap crests (some, not most)");
            var calm=new WavePlace();
            calm.Update(spec,300.25,new WindModel(new WorldShape(),new SeaConfig{SeaWindFrom=270,SeaWindStrength=0.1f}).SeaAt(50000,50000,3000),Wind.FromCompass(270,0.1));
            int calmCaps=0;for(int n=0;n<400;n++){var c=calm.CrestAt(50000+n*1.37,50000+n*0.61);if(WavePlace.Whitecap(c.Height,c.Compression)>=0.2)calmCaps++;}
            check(calmCaps==0||WaveSpectrum.WhitecapCoverage(0.1)<0.002,$"force 2: no spray (whitecap crests {calmCaps}, coverage {WaveSpectrum.WhitecapCoverage(0.1):F4} below the spray's 0.002)");
            double SprayShare(double f) {
                var pl=new WavePlace();pl.Update(spec,300.25,new WindModel(new WorldShape(),new SeaConfig{SeaWindFrom=270,SeaWindStrength=(float)f}).SeaAt(50000,50000,3000),Wind.FromCompass(270,f));
                int s=0;for(int n=0;n<1000;n++){var c=pl.CrestAt(50000+n*1.37,50000+n*0.61);if(WavePlace.SprayCrest(c.Height,c.Compression)>=0.2)s++;}
                return s/1000.0;
            }
            double s4=SprayShare(0.3),s8=SprayShare(0.7),s12=SprayShare(1.5);
            check(s4<0.03&&s8>0.05&&s12>0.05&&s12<0.2,$"spray crests: {100*s4:F1}% of the sea at force 4, {100*s8:F1}% at 8, {100*s12:F1}% at 12 (whitecap coverage then sets how many throw it: {WaveSpectrum.WhitecapCoverage(0.3):F3}, {WaveSpectrum.WhitecapCoverage(0.7):F3}, {WaveSpectrum.WhitecapCoverage(1.5):F3})");
            check(CelestialShader.FoamDeclarations.Contains("smoothstep(0.22,0.40,crest.y) * smoothstep(0.8,1.65,crest.x)"),
                "the spray's crest gate is the shader's (sea_CrestFoam's thresholds unchanged)");
        }
    }
}
