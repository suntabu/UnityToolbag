using System;
using UnityEngine;

namespace UnityToolbag.CoordinateSystems.SphericalCoordinate
{
    public class SphericalCoordinateSystem
    {

        public static Vector3 Convert(float radius, float theta, float phi)
        {
            var sc = new SphericalCoordinate(radius,phi,theta);

            return sc.ToCartesian();
        }
        
        
        public struct SphericalCoordinate
        {
            private float m_Radius;
            private float m_Theta;
            private float m_Phi;

            public SphericalCoordinate(float f, float phi1, float theta1)
            {
                m_Phi = phi1;
                m_Radius = f;
                m_Theta = theta1;
            }

            public float r
            {
                get { return m_Radius; }
            }

            public float theta
            {
                get { return m_Theta; }
            }

            public float phi
            {
                get { return m_Phi; }
            }


            public Vector3 ToCartesian()
            {
                return ToCartesian(this);
            }

            public static Vector3 ToCartesian(SphericalCoordinate sc)
            {
                var cosTheta = Mathf.Cos(sc.m_Theta);
                var cosPhi = Mathf.Cos(sc.m_Phi);
                var sinTheta = Mathf.Sin(sc.m_Theta);
                var sinPhi = Mathf.Sin(sc.m_Phi);

                var xzr = sc.m_Radius * sinTheta;
                var x = xzr * cosPhi;
                var z = xzr * sinPhi;
                var y = sc.m_Radius * cosTheta;

                return new Vector3(x, y, z);
            }

            public static SphericalCoordinate FromCartesian(Vector3 v)
            {
                float r, theta, phi;

                var xzrSqr = v.x * v.x + v.z * v.z;
                if (Mathf.Abs(xzrSqr) < Mathf.Epsilon)
                {
                    r = v.y;
                    theta = 0;
                    phi = 0;

                    return new SphericalCoordinate(r, phi, theta);
                }

                var xzr = Mathf.Sqrt(xzrSqr);

                phi = Mathf.Acos(v.x / xzr);

                r = Mathf.Sqrt(xzrSqr + v.y * v.y);

                theta = Mathf.Acos(xzr / r);

                return new SphericalCoordinate(r, phi, theta);
            }
        }
    }
}