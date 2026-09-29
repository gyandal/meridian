namespace Meridian.Transforms;

/// <summary>The quantiles forecast ranges need: the normal distribution's and Student's t.</summary>
internal static class Statistics
{
    /// <summary>The value a standard normal variable is below with probability <paramref name="p"/> (Acklam's
    /// rational approximation, relative error under 1.2e-9).</summary>
    public static double NormalQuantile(double p)
    {
        if (p is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(p), p, "A probability strictly between 0 and 1.");
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double low = 0.02425, high = 1 - low;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > high) return -NormalQuantile(1 - p);
        double r = p - 0.5, s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }

    /// <summary>The value Student's t with <paramref name="degreesOfFreedom"/> is below with probability
    /// <paramref name="p"/>: the CDF, through the regularised incomplete beta function, inverted by bisection.</summary>
    public static double StudentTQuantile(double p, double degreesOfFreedom)
    {
        if (p is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(p), p, "A probability strictly between 0 and 1.");
        if (p < 0.5) return -StudentTQuantile(1 - p, degreesOfFreedom);
        double lo = 0, hi = 1;
        while (StudentTCdf(hi, degreesOfFreedom) < p) hi *= 2;
        for (int i = 0; i < 200 && hi - lo > 1e-12 * Math.Max(1, hi); i++)
        {
            double mid = (lo + hi) / 2;
            if (StudentTCdf(mid, degreesOfFreedom) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    public static double StudentTCdf(double t, double v)
    {
        double tail = 0.5 * RegularizedBeta(v / (v + t * t), v / 2, 0.5);
        return t >= 0 ? 1 - tail : tail;
    }

    /// <summary>I_x(a, b), by the continued fraction (Lentz's method), with the symmetry that keeps it convergent.</summary>
    private static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2)
            ? front * BetaFraction(x, a, b) / a
            : 1 - front * BetaFraction(1 - x, b, a) / b;
    }

    private static double BetaFraction(double x, double a, double b)
    {
        const double tiny = 1e-300;
        double c = 1, d = 1 - (a + b) * x / (a + 1);
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1 / d;
        double h = d;
        for (int m = 1; m <= 1000; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((a + m2 - 1) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (a + b + m) * x / ((a + m2) * (a + m2 + 1));
            d = 1 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-15) break;
        }
        return h;
    }

    /// <summary>ln Γ(x), Lanczos approximation (g = 7, n = 9).</summary>
    private static double LogGamma(double x)
    {
        double[] g = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        if (x < 0.5) return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        x -= 1;
        double sum = g[0];
        for (int i = 1; i < 9; i++) sum += g[i] / (x + i);
        double t = x + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }
}
