using System.Collections.Generic;
using EP.Ner;

namespace EP.Demo.Core
{
	public class MyReferent : Referent
	{
		public MyReferent(string typ) : base(typ)
		{
		}

		public List<Participant> Participants { get; set; }

		public List<Contract> Contracts { get; set; }
	}
}