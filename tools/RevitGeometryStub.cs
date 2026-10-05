namespace Autodesk.Revit.DB
{
    internal class XYZ
    {
        public XYZ(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; private set; }
        public double Y { get; private set; }
        public double Z { get; private set; }
    }
}
