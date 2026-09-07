using Kiln.Core.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kiln.Runtime.Hosting
{
	public interface IGame
	{
		GameManifest Manifest { get; }
		public void ConfigureResources( /*ResourceManager r*/);
		public void ConfigureWorld(World w);
		public void ConfigureSystems(/*SystemsManager s*/);
		public void OnStart(GameContext ctx);
		public void OnShutdown();
	}
}
