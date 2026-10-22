namespace SoundManager.Core;

// RBJ Audio EQ Cookbook: low shelf at 120 Hz, high shelf at 4 kHz,
// presence peaking filter at 2.5 kHz. State is owned by the render thread.
internal sealed class Biquad
{
    private double b0, b1, b2, a1, a2, z1, z2;

    public Biquad(double frequency, double db, int kind)
    {
        double a = Math.Pow(10, db / 40), w = 2 * Math.PI * frequency / Mixer.SampleRate;
        double c = Math.Cos(w), s = Math.Sin(w), alpha = s / Math.Sqrt(2);
        double t = 2 * Math.Sqrt(a) * alpha, a0;
        if (kind == 0)
        {
            b0 = a * ((a + 1) - (a - 1) * c + t);
            b1 = 2 * a * ((a - 1) - (a + 1) * c);
            b2 = a * ((a + 1) - (a - 1) * c - t);
            a0 = (a + 1) + (a - 1) * c + t;
            a1 = -2 * ((a - 1) + (a + 1) * c);
            a2 = (a + 1) + (a - 1) * c - t;
        }
        else if (kind == 1)
        {
            b0 = a * ((a + 1) + (a - 1) * c + t);
            b1 = -2 * a * ((a - 1) + (a + 1) * c);
            b2 = a * ((a + 1) + (a - 1) * c - t);
            a0 = (a + 1) - (a - 1) * c + t;
            a1 = 2 * ((a - 1) - (a + 1) * c);
            a2 = (a + 1) - (a - 1) * c - t;
        }
        else
        {
            b0 = 1 + alpha * a; b1 = -2 * c; b2 = 1 - alpha * a;
            a0 = 1 + alpha / a; a1 = -2 * c; a2 = 1 - alpha / a;
        }
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
    }

    public float Process(float value)
    {
        double result = value * b0 + z1;
        z1 = value * b1 - a1 * result + z2;
        z2 = value * b2 - a2 * result;
        return (float)result;
    }
}
