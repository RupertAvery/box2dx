﻿/*
  Box2DX Copyright (c) 2008 Ihar Kalasouski http://code.google.com/p/box2dx
  Box2D original C++ version Copyright (c) 2006-2007 Erin Catto http://www.gphysics.com

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.
*/

/*
Position Correction Notes
=========================
I tried the several algorithms for position correction of the 2D revolute joint.
I looked at these systems:
- simple pendulum (1m diameter sphere on massless 5m stick) with initial angular velocity of 100 rad/s.
- suspension bridge with 30 1m long planks of length 1m.
- multi-link chain with 30 1m long links.

Here are the algorithms:

Baumgarte - A fraction of the position error is added to the velocity error. There is no
separate position solver.

Pseudo Velocities - After the velocity solver and position integration,
the position error, Jacobian, and effective mass are recomputed. Then
the velocity constraints are solved with pseudo velocities and a fraction
of the position error is added to the pseudo velocity error. The pseudo
velocities are initialized to zero and there is no warm-starting. After
the position solver, the pseudo velocities are added to the positions.
This is also called the First Order World method or the Position LCP method.

Modified Nonlinear Gauss-Seidel (NGS) - Like Pseudo Velocities except the
position error is re-computed for each constraint and the positions are updated
after the constraint is solved. The radius vectors (aka Jacobians) are
re-computed too (otherwise the algorithm has horrible instability). The pseudo
velocity states are not needed because they are effectively zero at the beginning
of each iteration. Since we have the current position error, we allow the
iterations to terminate early if the error becomes smaller than b2_linearSlop.

Full NGS or just NGS - Like Modified NGS except the effective mass are re-computed
each time a constraint is solved.

Here are the results:
Baumgarte - this is the cheapest algorithm but it has some stability problems,
especially with the bridge. The chain links separate easily close to the root
and they jitter as they struggle to pull together. This is one of the most common
methods in the field. The big drawback is that the position correction artificially
affects the momentum, thus leading to instabilities and false bounce. I used a
bias factor of 0.2. A larger bias factor makes the bridge less stable, a smaller
factor makes joints and contacts more spongy.

Pseudo Velocities - the is more stable than the Baumgarte method. The bridge is
stable. However, joints still separate with large angular velocities. Drag the
simple pendulum in a circle quickly and the joint will separate. The chain separates
easily and does not recover. I used a bias factor of 0.2. A larger value lead to
the bridge collapsing when a heavy cube drops on it.

Modified NGS - this algorithm is better in some ways than Baumgarte and Pseudo
Velocities, but in other ways it is worse. The bridge and chain are much more
stable, but the simple pendulum goes unstable at high angular velocities.

Full NGS - stable in all tests. The joints display good stiffness. The bridge
still sags, but this is better than infinite forces.

Recommendations
Pseudo Velocities are not really worthwhile because the bridge and chain cannot
recover from joint separation. In other cases the benefit over Baumgarte is small.

Modified NGS is not a robust method for the revolute joint due to the violent
instability seen in the simple pendulum. Perhaps it is viable with other constraint
types, especially scalar constraints where the effective mass is a scalar.

This leaves Baumgarte and Full NGS. Baumgarte has small, but manageable instabilities
and is very fast. I don't think we can escape Baumgarte, especially in highly
demanding cases where high constraint fidelity is not needed.

Full NGS is robust and easy on the eyes. I recommend this as an option for
higher fidelity simulation and certainly for suspension bridges and long chains.
Full NGS might be a good choice for ragdolls, especially motorized ragdolls where
joint separation can be problematic. The number of NGS iterations can be reduced
for better performance without harming robustness much.

Each joint in a can be handled differently in the position solver. So I recommend
a system where the user can select the algorithm on a per joint basis. I would
probably default to the slower Full NGS and let the user select the faster
Baumgarte method in performance critical scenarios.
*/

using System;
using System.Collections.Generic;
using System.Text;

using Box2DX.Common;
using Box2DX.Collision;

namespace Box2DX.Dynamics
{
	public class Island : IDisposable
	{
		public ContactListener _listener;

		public Body[] _bodies;
		public Contact[] _contacts;
		public Joint[] _joints;

		public int _bodyCount;
		public int _jointCount;
		public int _contactCount;

		public int _bodyCapacity;
		public int _contactCapacity;
		public int _jointCapacity;

		public int _positionIterationCount;

		public Island(int bodyCapacity, int contactCapacity, int jointCapacity, ContactListener listener)
		{
			_bodyCapacity = bodyCapacity;
			_contactCapacity = contactCapacity;
			_jointCapacity = jointCapacity;
			_bodyCount = 0;
			_contactCount = 0;
			_jointCount = 0;

			_listener = listener;

			_bodies = new Body[bodyCapacity];
			_contacts = new Contact[contactCapacity];
			_joints = new Joint[jointCapacity];

			_positionIterationCount = 0;
		}

		public void Dispose()
		{
			// Warning: the order should reverse the constructor order.
			_joints = null;
			_contacts = null;
			_bodies = null;
		}

		public void Clear()
		{
			_bodyCount = 0;
			_contactCount = 0;
			_jointCount = 0;
		}

		public void Solve(TimeStep step, Vector2 gravity, bool correctPositions, bool allowSleep)
		{
			// Integrate velocities and apply damping.
			for (int i = 0; i < _bodyCount; ++i)
			{
				Body b = _bodies[i];

				if (b.IsStatic())
					continue;

				// Integrate velocities.
				b._linearVelocity += step.Dt * (gravity + b._invMass * b._force);
				b._angularVelocity += step.Dt * b._invI * b._torque;

				// Reset forces.
				b._force.Set(0.0f, 0.0f);
				b._torque = 0.0f;

				// Apply damping.
				// ODE: dv/dt + c * v = 0
				// Solution: v(t) = v0 * exp(-c * t)
				// Time step: v(t + dt) = v0 * exp(-c * (t + dt)) = v0 * exp(-c * t) * exp(-c * dt) = v * exp(-c * dt)
				// v2 = exp(-c * dt) * v1
				// Taylor expansion:
				// v2 = (1.0f - c * dt) * v1
				b._linearVelocity *= Common.Math.Clamp(1.0f - step.Dt * b._linearDamping, 0.0f, 1.0f);
				b._angularVelocity *= Common.Math.Clamp(1.0f - step.Dt * b._angularDamping, 0.0f, 1.0f);

				// Check for large velocities.
				if (Vector2.Dot(b._linearVelocity, b._linearVelocity) > Settings.MaxLinearVelocitySquared)
				{
					b._linearVelocity.Normalize();
					b._linearVelocity *= Settings.MaxLinearVelocity;
				}

				if (b._angularVelocity * b._angularVelocity > Settings.MaxAngularVelocitySquared)
				{
					if (b._angularVelocity < 0.0f)
					{
						b._angularVelocity = -Settings.MaxAngularVelocity;
					}
					else
					{
						b._angularVelocity = Settings.MaxAngularVelocity;
					}
				}
			}

			ContactSolver contactSolver = new ContactSolver(step, _contacts, _contactCount);

			// Initialize velocity constraints.
			contactSolver.InitVelocityConstraints();

			for (int i = 0; i < _jointCount; ++i)
			{
				_joints[i].InitVelocityConstraints(step);
			}

			// Solve velocity constraints.
			for (int i = 0; i < step.MaxIterations; ++i)
			{
				contactSolver.SolveVelocityConstraints();

				for (int j = 0; j < _jointCount; ++j)
				{
					_joints[j].SolveVelocityConstraints(step);
				}
			}

			// Post-solve (store impulses for warm starting).
			contactSolver.FinalizeVelocityConstraints();

			// Integrate positions.
			for (int i = 0; i < _bodyCount; ++i)
			{
				Body b = _bodies[i];

				if (b.IsStatic())
					continue;

				// Store positions for continuous collision.
				b._sweep.C0 = b._sweep.C;
				b._sweep.A0 = b._sweep.A;

				// Integrate
				b._sweep.C += step.Dt * b._linearVelocity;
				b._sweep.A += step.Dt * b._angularVelocity;

				// Compute new transform
				b.SynchronizeTransform();

				// Note: shapes are synchronized later.
			}

			if (correctPositions)
			{
				// Initialize position constraints.
				// Contacts don't need initialization.
				for (int i = 0; i < _jointCount; ++i)
				{
					_joints[i].InitPositionConstraints();
				}

				// Iterate over constraints.
				for (_positionIterationCount = 0; _positionIterationCount < step.MaxIterations; ++_positionIterationCount)
				{
					bool contactsOkay = contactSolver.SolvePositionConstraints(Settings.ContactBaumgarte);

					bool jointsOkay = true;
					for (int i = 0; i < _jointCount; ++i)
					{
						bool jointOkay = _joints[i].SolvePositionConstraints();
						jointsOkay = jointsOkay && jointOkay;
					}

					if (contactsOkay && jointsOkay)
					{
						break;
					}
				}
			}

			Report(contactSolver._constraints);

			if (allowSleep)
			{
				float minSleepTime = Common.Math.FLT_MAX;

				float linTolSqr = Settings.LinearSleepTolerance * Settings.LinearSleepTolerance;
				float angTolSqr = Settings.AngularSleepTolerance * Settings.AngularSleepTolerance;

				for (int i = 0; i < _bodyCount; ++i)
				{
					Body b = _bodies[i];
					if (b._invMass == 0.0f)
					{
						continue;
					}

					if ((b._flags & Body.BodyFlags.AllowSleep) == 0)
					{
						b._sleepTime = 0.0f;
						minSleepTime = 0.0f;
					}

					if ((b._flags & Body.BodyFlags.AllowSleep) == 0 ||
						b._angularVelocity * b._angularVelocity > angTolSqr ||
						Vector2.Dot(b._linearVelocity, b._linearVelocity) > linTolSqr)
					{
						b._sleepTime = 0.0f;
						minSleepTime = 0.0f;
					}
					else
					{
						b._sleepTime += step.Dt;
						minSleepTime = Common.Math.Min(minSleepTime, b._sleepTime);
					}
				}

				if (minSleepTime >= Settings.TimeToSleep)
				{
					for (int i = 0; i < _bodyCount; ++i)
					{
						Body b = _bodies[i];
						b._flags |= Body.BodyFlags.Sleep;
						b._linearVelocity = Vector2.Zero;
						b._angularVelocity = 0.0f;
					}
				}
			}
		}

		public void SolveTOI(TimeStep subStep)
		{
			ContactSolver contactSolver = new ContactSolver(subStep, _contacts, _contactCount);

			// No warm starting needed for TOI events.

			// Solve velocity constraints.
			for (int i = 0; i < subStep.MaxIterations; ++i)
			{
				contactSolver.SolveVelocityConstraints();
			}

			// Don't store the TOI contact forces for warm starting
			// because they can be quite large.

			// Integrate positions.
			for (int i = 0; i < _bodyCount; ++i)
			{
				Body b = _bodies[i];

				if (b.IsStatic())
					continue;

				// Store positions for continuous collision.
				b._sweep.C0 = b._sweep.C;
				b._sweep.A0 = b._sweep.A;

				// Integrate
				b._sweep.C += subStep.Dt * b._linearVelocity;
				b._sweep.A += subStep.Dt * b._angularVelocity;

				// Compute new transform
				b.SynchronizeTransform();

				// Note: shapes are synchronized later.
			}

			// Solve position constraints.
			float k_toiBaumgarte = 0.75f;
			for (int i = 0; i < subStep.MaxIterations; ++i)
			{
				bool contactsOkay = contactSolver.SolvePositionConstraints(k_toiBaumgarte);
				if (contactsOkay)
				{
					break;
				}
			}

			Report(contactSolver._constraints);
		}

		public void Add(Body body)
		{
			Box2DXDebug.Assert(_bodyCount < _bodyCapacity);
			_bodies[_bodyCount++] = body;
		}

		public void Add(Contact contact)
		{
			Box2DXDebug.Assert(_contactCount < _contactCapacity);
			_contacts[_contactCount++] = contact;
		}

		public void Add(Joint joint)
		{
			Box2DXDebug.Assert(_jointCount < _jointCapacity);
			_joints[_jointCount++] = joint;
		}

		public void Report(ContactConstraint[] constraints)
		{
			if (_listener == null)
			{
				return;
			}

			for (int i = 0; i < _contactCount; ++i)
			{
				Contact c = _contacts[i];
				ContactConstraint cc = constraints[i];
				ContactPoint cp = new ContactPoint();
				cp.Shape1 = c.GetShape1();
				cp.Shape2 = c.GetShape2();
				Body b1 = cp.Shape1.GetBody();
				int manifoldCount = c.GetManifoldCount();
				Manifold manifolds = c.GetManifolds();
#warning "manifold array"
				//for (int j = 0; j < manifoldCount; ++j)
				if(manifoldCount>0)
				{
					Manifold manifold = manifolds;
					cp.Normal = manifold.Normal;
					for (int k = 0; k < manifold.PointCount; ++k)
					{
						ManifoldPoint point = manifold.Points[k];
						ContactConstraintPoint ccp = cc.Points[k];
						cp.Position = Common.Math.Mul(b1.GetXForm(), point.LocalPoint1);
						cp.Separation = point.Separation;

						// TOI constraint results are not stored, so get
						// the result from the constraint.
						cp.NormalForce = ccp.NormalForce;
						cp.TangentForce = ccp.TangentForce;

						if ((point.ID.Features.Flip & Collision.Collision.NewPoint) != 0)
						{
							point.ID.Features.Flip &= (byte)~Collision.Collision.NewPoint;
							cp.ID = point.ID;
							_listener.Add(cp);
						}
						else
						{
							cp.ID = point.ID;
							_listener.Persist(cp);
						}
					}
				}
			}
		}
	}
}