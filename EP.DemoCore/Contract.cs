using System;
using System.Collections.Generic;

namespace EP.Demo.Core
{
	public class Contract
	{
		public string Number { get; set; }

		public List<DateTime?> Dates { get; set; } = new List<DateTime?>();
	}
}