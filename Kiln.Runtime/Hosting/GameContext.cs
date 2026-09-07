using Kiln.Core.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kiln.Runtime.Hosting
{
	public class GameContext
	{
		private World _world;

		public World World { get => _world; }
	}
}
