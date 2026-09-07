using Kiln.Core.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kiln.Core.Simulation
{
	public interface ISystem
	{
		void Initialise(World world);
		void OnStep(World world);
	}
}
