//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using Nethermind.Network;
using Nethermind.Stats.Model;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<Capability> Caps { get; set; }
        public PeerNetworkInfo Network { get; set; }
        public string Protocols { get; set; }
        
        public PeerInfo(Peer peer)
        {
            if (peer.Node == null)
            {
                throw new ArgumentException(
                    $"{nameof(PeerInfo)} cannot be created for a {nameof(Peer)} with an unknown {peer.Node}");
            }
            
            Id = peer.InSession?.RemoteNodeId.ToString() ?? peer.OutSession?.RemoteNodeId.ToString();
            Name = peer.Node.ClientId;
            Network = new PeerNetworkInfo(peer);
        }
    }
}